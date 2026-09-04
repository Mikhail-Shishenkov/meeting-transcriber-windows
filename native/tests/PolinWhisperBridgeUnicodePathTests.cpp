#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>

#include <Windows.h>

int32_t pmt_whisper_test_validate_wav(const char * path);

namespace {

void write_u16(std::ofstream & stream, uint16_t value) {
    const char bytes[] = {
        static_cast<char>(value & 0xffU),
        static_cast<char>((value >> 8U) & 0xffU),
    };
    stream.write(bytes, sizeof(bytes));
}

void write_u32(std::ofstream & stream, uint32_t value) {
    const char bytes[] = {
        static_cast<char>(value & 0xffU),
        static_cast<char>((value >> 8U) & 0xffU),
        static_cast<char>((value >> 16U) & 0xffU),
        static_cast<char>((value >> 24U) & 0xffU),
    };
    stream.write(bytes, sizeof(bytes));
}

bool write_valid_wav(const std::filesystem::path & path) {
    std::ofstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    stream.write("RIFF", 4);
    write_u32(stream, 38);
    stream.write("WAVE", 4);
    stream.write("fmt ", 4);
    write_u32(stream, 16);
    write_u16(stream, 1);
    write_u16(stream, 1);
    write_u32(stream, 16'000);
    write_u32(stream, 32'000);
    write_u16(stream, 2);
    write_u16(stream, 16);
    stream.write("data", 4);
    write_u32(stream, 2);
    write_u16(stream, 0);
    return stream.good();
}

}  // namespace

int main() {
    const std::filesystem::path root =
        std::filesystem::temp_directory_path()
        / (L"pmtwhisper Unicode path тест "
            + std::to_wstring(GetCurrentProcessId()));
    const std::filesystem::path wav_path =
        root / L"WAV с кириллицей и пробелами.wav";
    std::error_code error;
    std::filesystem::remove_all(root, error);
    error.clear();
    if (!std::filesystem::create_directories(root, error) || error) {
        std::cerr << "Could not create Unicode test directory.\n";
        return 1;
    }

    const bool written = write_valid_wav(wav_path);
    const std::string utf8_path = wav_path.u8string();
    const int32_t status = written
        ? pmt_whisper_test_validate_wav(utf8_path.c_str())
        : -1;

    error.clear();
    std::filesystem::remove_all(root, error);
    if (!written) {
        std::cerr << "Could not write Unicode-path WAV fixture.\n";
        return 2;
    }
    if (status != 0) {
        std::cerr << "UTF-8 Unicode-path WAV validation returned "
                  << status << ".\n";
        return 3;
    }
    if (error) {
        std::cerr << "Could not remove Unicode-path WAV fixture.\n";
        return 4;
    }

    std::cout << "Unicode-path WAV bridge regression passed.\n";
    return 0;
}
