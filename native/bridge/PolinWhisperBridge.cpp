#include "PolinWhisperBridge.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <new>
#include <string>
#include <vector>

#if defined(POLIN_WHISPER_RUNTIME_ENABLED) && \
    POLIN_WHISPER_RUNTIME_ENABLED
#include "whisper.h"
#endif

namespace {

struct Segment {
    int64_t start_milliseconds;
    int64_t end_milliseconds;
    std::string text;
};

constexpr int64_t kSampleRate = 16'000;
constexpr int64_t kMillisecondsPerSecond = 1'000;
// Each core owns 15 minutes of the final timeline. The surrounding audio is
// decoded only as boundary context, so overlap segments are never exported twice.
constexpr int64_t kTranscriptionChunkMilliseconds = 15 * 60 * 1'000;
constexpr int64_t kTranscriptionOverlapMilliseconds = 15 * 1'000;
constexpr int64_t kChunkSamples =
    kSampleRate * kTranscriptionChunkMilliseconds
    / kMillisecondsPerSecond;
constexpr int64_t kOverlapSamples =
    kSampleRate * kTranscriptionOverlapMilliseconds
    / kMillisecondsPerSecond;

struct ChunkWindow {
    int64_t core_start_sample;
    int64_t core_end_sample;
    int64_t read_start_sample;
    int64_t read_end_sample;
};

enum class WavReadStatus {
    success,
    invalid,
    unsupported,
};

int64_t samples_to_milliseconds(int64_t samples) {
    return samples * kMillisecondsPerSecond / kSampleRate;
}

size_t transcription_chunk_count(int64_t sample_count) {
    if (sample_count <= 0) {
        return 0;
    }
    return static_cast<size_t>(
        (sample_count + kChunkSamples - 1) / kChunkSamples
    );
}

bool transcription_chunk_window(
    int64_t sample_count,
    size_t index,
    ChunkWindow & window
) {
    const size_t count = transcription_chunk_count(sample_count);
    if (index >= count
        || index
            > static_cast<size_t>(
                std::numeric_limits<int64_t>::max() / kChunkSamples
            )) {
        return false;
    }
    const int64_t core_start =
        static_cast<int64_t>(index) * kChunkSamples;
    const int64_t core_end = std::min(
        sample_count,
        core_start + kChunkSamples
    );
    window = ChunkWindow{
        core_start,
        core_end,
        std::max<int64_t>(0, core_start - kOverlapSamples),
        std::min(sample_count, core_end + kOverlapSamples),
    };
    return true;
}

bool absolute_segment_for_window(
    const ChunkWindow & window,
    int64_t total_duration_milliseconds,
    bool final_chunk,
    int64_t local_start_milliseconds,
    int64_t local_end_milliseconds,
    int64_t & absolute_start_milliseconds,
    int64_t & absolute_end_milliseconds
) {
    if (local_start_milliseconds < 0
        || local_end_milliseconds < local_start_milliseconds) {
        return false;
    }
    const int64_t read_start_milliseconds =
        samples_to_milliseconds(window.read_start_sample);
    if (local_start_milliseconds
            > std::numeric_limits<int64_t>::max()
                - read_start_milliseconds
        || local_end_milliseconds
            > std::numeric_limits<int64_t>::max()
                - read_start_milliseconds) {
        return false;
    }
    const int64_t candidate_start =
        read_start_milliseconds + local_start_milliseconds;
    const int64_t candidate_end =
        read_start_milliseconds + local_end_milliseconds;
    const int64_t midpoint = candidate_start
        + (candidate_end - candidate_start) / 2;
    const int64_t core_start_milliseconds =
        samples_to_milliseconds(window.core_start_sample);
    const int64_t core_end_milliseconds =
        samples_to_milliseconds(window.core_end_sample);
    const bool belongs_to_core = midpoint >= core_start_milliseconds
        && (midpoint < core_end_milliseconds
            || (final_chunk && midpoint <= core_end_milliseconds));
    if (!belongs_to_core || midpoint > total_duration_milliseconds) {
        return false;
    }
    absolute_start_milliseconds = std::max(
        candidate_start,
        core_start_milliseconds
    );
    absolute_start_milliseconds = std::min(
        absolute_start_milliseconds,
        total_duration_milliseconds
    );
    absolute_end_milliseconds = std::min({
        candidate_end,
        core_end_milliseconds,
        total_duration_milliseconds,
    });
    return absolute_end_milliseconds >= absolute_start_milliseconds;
}

uint16_t read_u16(const uint8_t * bytes) {
    return static_cast<uint16_t>(bytes[0])
        | static_cast<uint16_t>(
            static_cast<uint16_t>(bytes[1]) << 8
        );
}

uint32_t read_u32(const uint8_t * bytes) {
    return static_cast<uint32_t>(bytes[0])
        | (static_cast<uint32_t>(bytes[1]) << 8)
        | (static_cast<uint32_t>(bytes[2]) << 16)
        | (static_cast<uint32_t>(bytes[3]) << 24);
}

bool read_exact(
    std::ifstream & stream,
    void * destination,
    std::streamsize byte_count
) {
    stream.read(
        static_cast<char *>(destination),
        byte_count
    );
    return stream.gcount() == byte_count;
}

class PCM16Mono16kHzWavReader {
public:
    explicit PCM16Mono16kHzWavReader(const char * path)
        : stream_(
            path == nullptr
                ? std::filesystem::path()
                : std::filesystem::u8path(path),
            std::ios::binary
        ) {
        status_ = inspect();
    }

    WavReadStatus status() const {
        return status_;
    }

    int64_t sample_count() const {
        return sample_count_;
    }

    WavReadStatus read_samples(
        int64_t first_sample,
        int64_t requested_sample_count,
        std::vector<float> & samples
    ) {
        if (status_ != WavReadStatus::success
            || first_sample < 0
            || requested_sample_count <= 0
            || first_sample > sample_count_
            || requested_sample_count > sample_count_ - first_sample
            || requested_sample_count
                > static_cast<int64_t>(
                    std::numeric_limits<int>::max()
                )) {
            return WavReadStatus::invalid;
        }
        const uint64_t byte_offset = static_cast<uint64_t>(first_sample) * 2;
        const uint64_t byte_count =
            static_cast<uint64_t>(requested_sample_count) * 2;
        if (byte_offset
                > static_cast<uint64_t>(
                    std::numeric_limits<std::streamoff>::max()
                )
            || byte_count
                > static_cast<uint64_t>(
                    std::numeric_limits<std::streamsize>::max()
                )) {
            return WavReadStatus::unsupported;
        }

        std::vector<uint8_t> pcm(static_cast<size_t>(byte_count));
        stream_.clear();
        stream_.seekg(
            data_position_
                + static_cast<std::streamoff>(byte_offset)
        );
        if (!read_exact(
                stream_,
                pcm.data(),
                static_cast<std::streamsize>(byte_count)
            )) {
            return WavReadStatus::invalid;
        }

        samples.resize(static_cast<size_t>(requested_sample_count));
        for (size_t index = 0; index < samples.size(); ++index) {
            const uint16_t raw = read_u16(pcm.data() + index * 2);
            const int16_t signed_sample = static_cast<int16_t>(raw);
            samples[index] =
                static_cast<float>(signed_sample) / 32768.0F;
        }
        return WavReadStatus::success;
    }

private:
    WavReadStatus inspect() {
        if (!stream_) {
            return WavReadStatus::invalid;
        }

        uint8_t header[12];
        if (!read_exact(stream_, header, sizeof(header))
            || std::memcmp(header, "RIFF", 4) != 0
            || std::memcmp(header + 8, "WAVE", 4) != 0) {
            return WavReadStatus::invalid;
        }
        stream_.seekg(0, std::ios::end);
        const std::streamoff file_size =
            static_cast<std::streamoff>(stream_.tellg());
        if (file_size < 12
            || read_u32(header + 4)
                != static_cast<uint64_t>(file_size - 8)) {
            return WavReadStatus::invalid;
        }
        stream_.seekg(12, std::ios::beg);

        bool has_format = false;
        bool has_data = false;
        uint16_t audio_format = 0;
        uint16_t channel_count = 0;
        uint32_t sample_rate = 0;
        uint32_t byte_rate = 0;
        uint16_t block_alignment = 0;
        uint16_t bits_per_sample = 0;
        std::streampos data_position = 0;
        uint32_t data_size = 0;

        while (stream_ && (!has_format || !has_data)) {
            uint8_t chunk_header[8];
            if (!read_exact(
                    stream_,
                    chunk_header,
                    sizeof(chunk_header)
                )) {
                break;
            }
            const uint32_t chunk_size = read_u32(chunk_header + 4);
            const std::streampos payload_position = stream_.tellg();

            if (std::memcmp(chunk_header, "fmt ", 4) == 0) {
                if (chunk_size < 16) {
                    return WavReadStatus::invalid;
                }
                uint8_t format[16];
                if (!read_exact(stream_, format, sizeof(format))) {
                    return WavReadStatus::invalid;
                }
                audio_format = read_u16(format);
                channel_count = read_u16(format + 2);
                sample_rate = read_u32(format + 4);
                byte_rate = read_u32(format + 8);
                block_alignment = read_u16(format + 12);
                bits_per_sample = read_u16(format + 14);
                has_format = true;
            } else if (std::memcmp(chunk_header, "data", 4) == 0) {
                data_position = payload_position;
                data_size = chunk_size;
                has_data = true;
            }

            const uint64_t padded_size =
                static_cast<uint64_t>(chunk_size) + (chunk_size & 1U);
            if (padded_size
                > static_cast<uint64_t>(
                    std::numeric_limits<std::streamoff>::max()
                )) {
                return WavReadStatus::invalid;
            }
            const std::streamoff payload_offset =
                static_cast<std::streamoff>(payload_position);
            if (payload_offset < 0
                || payload_offset > file_size
                || padded_size
                    > static_cast<uint64_t>(
                        file_size - payload_offset
                    )) {
                return WavReadStatus::invalid;
            }
            stream_.seekg(
                payload_position
                    + static_cast<std::streamoff>(padded_size)
            );
        }

        if (!has_format || !has_data || data_size == 0) {
            return WavReadStatus::invalid;
        }
        if (audio_format != 1
            || channel_count != 1
            || sample_rate != kSampleRate
            || byte_rate != 32'000
            || block_alignment != 2
            || bits_per_sample != 16
            || data_size % 2 != 0) {
            return WavReadStatus::unsupported;
        }

        data_position_ = data_position;
        sample_count_ = static_cast<int64_t>(data_size / 2);
        return WavReadStatus::success;
    }

    std::ifstream stream_;
    WavReadStatus status_ = WavReadStatus::invalid;
    std::streampos data_position_ = 0;
    int64_t sample_count_ = 0;
};

bool timestamp_range_in_milliseconds(
    int64_t start_units,
    int64_t end_units,
    int64_t & start_milliseconds,
    int64_t & end_milliseconds
) {
    constexpr int64_t units_to_milliseconds = 10;
    if (start_units < 0
        || end_units < start_units
        || start_units
            > std::numeric_limits<int64_t>::max()
                / units_to_milliseconds
        || end_units
            > std::numeric_limits<int64_t>::max()
                / units_to_milliseconds) {
        return false;
    }
    start_milliseconds = start_units * units_to_milliseconds;
    end_milliseconds = end_units * units_to_milliseconds;
    return true;
}

}  // namespace

struct PMTWhisperSession {
#if defined(POLIN_WHISPER_RUNTIME_ENABLED) && \
    POLIN_WHISPER_RUNTIME_ENABLED
    whisper_context * context = nullptr;
#endif
    std::atomic<bool> cancellation_requested{false};
    std::vector<Segment> segments;
    std::string detected_language;
    PMTWhisperProgressCallback progress_callback = nullptr;
    void * progress_user_data = nullptr;
    float last_progress = 0;
    float chunk_progress_base = 0;
    float chunk_progress_span = 1;
};

#if defined(POLIN_WHISPER_RUNTIME_ENABLED) && \
    POLIN_WHISPER_RUNTIME_ENABLED

namespace {

void whisper_progress(
    whisper_context *,
    whisper_state *,
    int progress,
    void * user_data
) {
    auto * session = static_cast<PMTWhisperSession *>(user_data);
    if (session == nullptr || session->progress_callback == nullptr) {
        return;
    }
    const float local_value = std::clamp(
        static_cast<float>(progress) / 100.0F,
        0.0F,
        1.0F
    );
    const float value = std::clamp(
        session->chunk_progress_base
            + session->chunk_progress_span * local_value,
        session->last_progress,
        1.0F
    );
    if (value <= session->last_progress) {
        return;
    }
    session->last_progress = value;
    session->progress_callback(
        value,
        session->progress_user_data
    );
}

bool whisper_should_abort(void * user_data) {
    const auto * session =
        static_cast<const PMTWhisperSession *>(user_data);
    return session != nullptr
        && session->cancellation_requested.load(
            std::memory_order_relaxed
        );
}

class ProgressCallbackReset {
public:
    explicit ProgressCallbackReset(PMTWhisperSession * session)
        : session_(session) {
    }

    ~ProgressCallbackReset() {
        session_->progress_callback = nullptr;
        session_->progress_user_data = nullptr;
    }

private:
    PMTWhisperSession * session_;
};

}  // namespace

#endif

#if defined(POLIN_WHISPER_BRIDGE_TESTING)

int32_t pmt_whisper_test_validate_wav(const char * path) {
    try {
        return static_cast<int32_t>(
            PCM16Mono16kHzWavReader(path).status()
        );
    } catch (...) {
        return -1;
    }
}

size_t pmt_whisper_test_chunk_count(int64_t sample_count) {
    return transcription_chunk_count(sample_count);
}

int32_t pmt_whisper_test_chunk_window(
    int64_t sample_count,
    size_t index,
    int64_t * core_start_sample,
    int64_t * core_end_sample,
    int64_t * read_start_sample,
    int64_t * read_end_sample
) {
    if (core_start_sample == nullptr
        || core_end_sample == nullptr
        || read_start_sample == nullptr
        || read_end_sample == nullptr) {
        return 0;
    }
    ChunkWindow window{};
    if (!transcription_chunk_window(sample_count, index, window)) {
        return 0;
    }
    *core_start_sample = window.core_start_sample;
    *core_end_sample = window.core_end_sample;
    *read_start_sample = window.read_start_sample;
    *read_end_sample = window.read_end_sample;
    return 1;
}

int32_t pmt_whisper_test_absolute_segment(
    int64_t core_start_sample,
    int64_t core_end_sample,
    int64_t read_start_sample,
    int64_t read_end_sample,
    int64_t total_duration_milliseconds,
    int32_t final_chunk,
    int64_t local_start_milliseconds,
    int64_t local_end_milliseconds,
    int64_t * absolute_start_milliseconds,
    int64_t * absolute_end_milliseconds
) {
    if (absolute_start_milliseconds == nullptr
        || absolute_end_milliseconds == nullptr) {
        return 0;
    }
    return absolute_segment_for_window(
        ChunkWindow{
            core_start_sample,
            core_end_sample,
            read_start_sample,
            read_end_sample,
        },
        total_duration_milliseconds,
        final_chunk != 0,
        local_start_milliseconds,
        local_end_milliseconds,
        *absolute_start_milliseconds,
        *absolute_end_milliseconds
    ) ? 1 : 0;
}

int32_t pmt_whisper_test_timestamp_range(
    int64_t start_units,
    int64_t end_units,
    int64_t * start_milliseconds,
    int64_t * end_milliseconds
) {
    if (start_milliseconds == nullptr
        || end_milliseconds == nullptr) {
        return 0;
    }
    return timestamp_range_in_milliseconds(
        start_units,
        end_units,
        *start_milliseconds,
        *end_milliseconds
    ) ? 1 : 0;
}

#endif

int32_t pmt_whisper_runtime_available(void) {
#if defined(POLIN_WHISPER_RUNTIME_ENABLED) && \
    POLIN_WHISPER_RUNTIME_ENABLED
    return 1;
#else
    return 0;
#endif
}

int32_t pmt_whisper_session_create(
    const char * model_path,
    PMTWhisperSession ** session
) {
    if (model_path == nullptr || session == nullptr) {
        return PMT_WHISPER_STATUS_INVALID_ARGUMENT;
    }
    *session = nullptr;

#if defined(POLIN_WHISPER_RUNTIME_ENABLED) && \
    POLIN_WHISPER_RUNTIME_ENABLED
    whisper_context * context = nullptr;
    try {
        whisper_context_params parameters =
            whisper_context_default_params();
        parameters.use_gpu = true;
        context = whisper_init_from_file_with_params_no_state(
            model_path,
            parameters
        );
        if (context == nullptr) {
            return PMT_WHISPER_STATUS_MODEL_LOAD_FAILED;
        }

        auto * created = new (std::nothrow) PMTWhisperSession();
        if (created == nullptr) {
            auto * context_to_free = context;
            context = nullptr;
            whisper_free(context_to_free);
            return PMT_WHISPER_STATUS_INFERENCE_FAILED;
        }
        created->context = context;
        *session = created;
        return PMT_WHISPER_STATUS_OK;
    } catch (...) {
        if (context != nullptr) {
            try {
                whisper_free(context);
            } catch (...) {
            }
        }
        return PMT_WHISPER_STATUS_MODEL_LOAD_FAILED;
    }
#else
    return PMT_WHISPER_STATUS_RUNTIME_UNAVAILABLE;
#endif
}

void pmt_whisper_session_destroy(PMTWhisperSession * session) {
    if (session == nullptr) {
        return;
    }
#if defined(POLIN_WHISPER_RUNTIME_ENABLED) && \
    POLIN_WHISPER_RUNTIME_ENABLED
    try {
        whisper_free(session->context);
    } catch (...) {
    }
    session->context = nullptr;
#endif
    delete session;
}

void pmt_whisper_session_request_cancel(
    PMTWhisperSession * session
) {
    if (session != nullptr) {
        session->cancellation_requested.store(
            true,
            std::memory_order_relaxed
        );
    }
}

int32_t pmt_whisper_session_transcribe_wav(
    PMTWhisperSession * session,
    const char * wav_path,
    const char * language,
    int32_t thread_count,
    PMTWhisperProgressCallback progress_callback,
    void * progress_user_data
) {
    if (session == nullptr || wav_path == nullptr) {
        return PMT_WHISPER_STATUS_INVALID_ARGUMENT;
    }

#if defined(POLIN_WHISPER_RUNTIME_ENABLED) && \
    POLIN_WHISPER_RUNTIME_ENABLED
    try {
        PCM16Mono16kHzWavReader reader(wav_path);
        switch (reader.status()) {
        case WavReadStatus::invalid:
            return PMT_WHISPER_STATUS_INVALID_WAV;
        case WavReadStatus::unsupported:
            return PMT_WHISPER_STATUS_UNSUPPORTED_WAV;
        case WavReadStatus::success:
            break;
        }
        if (session->cancellation_requested.load(
            std::memory_order_relaxed
        )) {
            return PMT_WHISPER_STATUS_CANCELLED;
        }

        session->segments.clear();
        session->detected_language.clear();
        session->progress_callback = progress_callback;
        session->progress_user_data = progress_user_data;
        ProgressCallbackReset callback_reset(session);
        session->last_progress = 0;
        const bool automatic_language =
            language == nullptr
            || language[0] == '\0'
            || std::strcmp(language, "auto") == 0;
        const int64_t total_samples = reader.sample_count();
        const int64_t total_duration_milliseconds =
            samples_to_milliseconds(total_samples);
        const size_t chunk_count =
            transcription_chunk_count(total_samples);
        if (chunk_count == 0) {
            return PMT_WHISPER_STATUS_INVALID_WAV;
        }

        for (size_t chunk_index = 0;
             chunk_index < chunk_count;
             ++chunk_index) {
            if (session->cancellation_requested.load(
                    std::memory_order_relaxed
                )) {
                return PMT_WHISPER_STATUS_CANCELLED;
            }
            ChunkWindow window{};
            if (!transcription_chunk_window(
                    total_samples,
                    chunk_index,
                    window
                )) {
                return PMT_WHISPER_STATUS_INVALID_RESULT;
            }
            std::vector<float> samples;
            switch (reader.read_samples(
                window.read_start_sample,
                window.read_end_sample - window.read_start_sample,
                samples
            )) {
            case WavReadStatus::invalid:
                return PMT_WHISPER_STATUS_INVALID_WAV;
            case WavReadStatus::unsupported:
                return PMT_WHISPER_STATUS_UNSUPPORTED_WAV;
            case WavReadStatus::success:
                break;
            }

            // A fresh decoder state prevents hypotheses from a bad or silent
            // chunk from contaminating every later chunk in a long recording.
            std::unique_ptr<whisper_state, decltype(&whisper_free_state)>
                state(
                    whisper_init_state(session->context),
                    whisper_free_state
                );
            if (!state) {
                return PMT_WHISPER_STATUS_INFERENCE_FAILED;
            }

            whisper_full_params parameters =
                whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
            parameters.n_threads = std::max<int32_t>(thread_count, 1);
            parameters.translate = false;
            parameters.no_context = true;
            parameters.no_timestamps = false;
            parameters.single_segment = false;
            parameters.print_special = false;
            parameters.print_progress = false;
            parameters.print_realtime = false;
            parameters.print_timestamps = false;
            parameters.token_timestamps = false;
            parameters.language = automatic_language ? "auto" : language;
            parameters.detect_language = false;
            parameters.progress_callback = whisper_progress;
            parameters.progress_callback_user_data = session;
            parameters.abort_callback = whisper_should_abort;
            parameters.abort_callback_user_data = session;

            session->chunk_progress_base = static_cast<float>(
                static_cast<double>(window.core_start_sample)
                    / static_cast<double>(total_samples)
            );
            session->chunk_progress_span = static_cast<float>(
                static_cast<double>(
                    window.core_end_sample - window.core_start_sample
                ) / static_cast<double>(total_samples)
            );
            const int inference_result = whisper_full_with_state(
                session->context,
                state.get(),
                parameters,
                samples.data(),
                static_cast<int>(samples.size())
            );
            if (session->cancellation_requested.load(
                    std::memory_order_relaxed
                )) {
                return PMT_WHISPER_STATUS_CANCELLED;
            }
            if (inference_result != 0) {
                return PMT_WHISPER_STATUS_INFERENCE_FAILED;
            }

            const int segment_count =
                whisper_full_n_segments_from_state(state.get());
            for (int index = 0; index < segment_count; ++index) {
                int64_t local_start_milliseconds = 0;
                int64_t local_end_milliseconds = 0;
                if (!timestamp_range_in_milliseconds(
                        whisper_full_get_segment_t0_from_state(
                            state.get(),
                            index
                        ),
                        whisper_full_get_segment_t1_from_state(
                            state.get(),
                            index
                        ),
                        local_start_milliseconds,
                        local_end_milliseconds
                    )) {
                    session->segments.clear();
                    return PMT_WHISPER_STATUS_INVALID_RESULT;
                }
                int64_t absolute_start_milliseconds = 0;
                int64_t absolute_end_milliseconds = 0;
                if (!absolute_segment_for_window(
                        window,
                        total_duration_milliseconds,
                        chunk_index + 1 == chunk_count,
                        local_start_milliseconds,
                        local_end_milliseconds,
                        absolute_start_milliseconds,
                        absolute_end_milliseconds
                    )) {
                    continue;
                }
                const char * text =
                    whisper_full_get_segment_text_from_state(
                        state.get(),
                        index
                    );
                session->segments.push_back(
                    Segment{
                        absolute_start_milliseconds,
                        absolute_end_milliseconds,
                        text == nullptr ? "" : text,
                    }
                );
            }

            if (session->detected_language.empty()) {
                const int language_id =
                    whisper_full_lang_id_from_state(state.get());
                const char * detected = whisper_lang_str(language_id);
                if (detected != nullptr) {
                    session->detected_language = detected;
                }
            }
        }

        if (session->cancellation_requested.load(
            std::memory_order_relaxed
        )) {
            return PMT_WHISPER_STATUS_CANCELLED;
        }
        if (progress_callback != nullptr
            && session->last_progress < 1.0F) {
            session->last_progress = 1.0F;
            progress_callback(1.0F, progress_user_data);
        }
        return PMT_WHISPER_STATUS_OK;
    } catch (...) {
        return session->cancellation_requested.load(
            std::memory_order_relaxed
        )
            ? PMT_WHISPER_STATUS_CANCELLED
            : PMT_WHISPER_STATUS_INFERENCE_FAILED;
    }
#else
    (void) language;
    (void) thread_count;
    (void) progress_callback;
    (void) progress_user_data;
    return PMT_WHISPER_STATUS_RUNTIME_UNAVAILABLE;
#endif
}

size_t pmt_whisper_session_segment_count(
    const PMTWhisperSession * session
) {
    return session == nullptr ? 0 : session->segments.size();
}

int64_t pmt_whisper_session_segment_start_milliseconds(
    const PMTWhisperSession * session,
    size_t index
) {
    if (session == nullptr || index >= session->segments.size()) {
        return -1;
    }
    return session->segments[index].start_milliseconds;
}

int64_t pmt_whisper_session_segment_end_milliseconds(
    const PMTWhisperSession * session,
    size_t index
) {
    if (session == nullptr || index >= session->segments.size()) {
        return -1;
    }
    return session->segments[index].end_milliseconds;
}

const char * pmt_whisper_session_segment_text(
    const PMTWhisperSession * session,
    size_t index
) {
    if (session == nullptr || index >= session->segments.size()) {
        return nullptr;
    }
    return session->segments[index].text.c_str();
}

const char * pmt_whisper_session_detected_language(
    const PMTWhisperSession * session
) {
    if (session == nullptr || session->detected_language.empty()) {
        return nullptr;
    }
    return session->detected_language.c_str();
}
