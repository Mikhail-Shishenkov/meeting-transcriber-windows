#ifndef POLIN_WHISPER_BRIDGE_H
#define POLIN_WHISPER_BRIDGE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct PMTWhisperSession PMTWhisperSession;

typedef void (*PMTWhisperProgressCallback)(
    float progress,
    void * user_data
);

enum {
    PMT_WHISPER_STATUS_OK = 0,
    PMT_WHISPER_STATUS_INVALID_ARGUMENT = 1,
    PMT_WHISPER_STATUS_RUNTIME_UNAVAILABLE = 2,
    PMT_WHISPER_STATUS_MODEL_LOAD_FAILED = 3,
    PMT_WHISPER_STATUS_INVALID_WAV = 4,
    PMT_WHISPER_STATUS_UNSUPPORTED_WAV = 5,
    PMT_WHISPER_STATUS_INFERENCE_FAILED = 6,
    PMT_WHISPER_STATUS_CANCELLED = 7,
    PMT_WHISPER_STATUS_INVALID_RESULT = 8,
};

int32_t pmt_whisper_runtime_available(void);

/*
 * On success, the caller owns the returned session and must destroy it
 * exactly once with pmt_whisper_session_destroy().
 */
int32_t pmt_whisper_session_create(
    const char * model_path,
    PMTWhisperSession ** session
);

void pmt_whisper_session_destroy(PMTWhisperSession * session);

void pmt_whisper_session_request_cancel(
    PMTWhisperSession * session
);

int32_t pmt_whisper_session_transcribe_wav(
    PMTWhisperSession * session,
    const char * wav_path,
    const char * language,
    int32_t thread_count,
    PMTWhisperProgressCallback progress_callback,
    void * progress_user_data
);

size_t pmt_whisper_session_segment_count(
    const PMTWhisperSession * session
);

int64_t pmt_whisper_session_segment_start_milliseconds(
    const PMTWhisperSession * session,
    size_t index
);

int64_t pmt_whisper_session_segment_end_milliseconds(
    const PMTWhisperSession * session,
    size_t index
);

/*
 * Returned UTF-8 strings are borrowed from the session. They remain valid
 * until the next transcription on that session or until the session is
 * destroyed. Callers must not free them.
 */
const char * pmt_whisper_session_segment_text(
    const PMTWhisperSession * session,
    size_t index
);

const char * pmt_whisper_session_detected_language(
    const PMTWhisperSession * session
);

#ifdef __cplusplus
}
#endif

#endif
