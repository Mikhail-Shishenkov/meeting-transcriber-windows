# Windows spike — результаты

## VERIFIED MANUALLY ON REAL WINDOWS 10 x64

- Visual Studio 2022 Build Tools / MSVC работает.
- CMake работает.
- .NET SDK 10 работает.
- whisper.cpp v1.9.1 (commit `f049fff95a089aa9969deb009cdd4892b3e74916`) успешно собран CPU-only.
- Модель `ggml-small.bin`:
  - size: 487601967 bytes
  - SHA-256: `1BE3A9B2063867B937E64E2EC7483364A79917E157FA98C5D94B5C1FFFEA987B`
- raw whisper-cli успешно транскрибировал WAV-образец на CPU.
- `PolinWhisperBridge.cpp` из macOS-reference скомпилирован MSVC.
- Native bridge smoke пройден успешно: `"whisper bridge smoke passed"`.
- `pmtwhisper.dll` успешно собран.
- dumpbin подтвердил 10 экспортов:
  1. `pmt_whisper_runtime_available`
  2. `pmt_whisper_session_create`
  3. `pmt_whisper_session_destroy`
  4. `pmt_whisper_session_detected_language`
  5. `pmt_whisper_session_request_cancel`
  6. `pmt_whisper_session_segment_count`
  7. `pmt_whisper_session_segment_end_milliseconds`
  8. `pmt_whisper_session_segment_start_milliseconds`
  9. `pmt_whisper_session_segment_text`
  10. `pmt_whisper_session_transcribe_wav`

## NOT LOCALLY VERIFIABLE ON MAC

Все проверки из раздела выше (MSVC, CMake, .NET SDK 10, сборка whisper.cpp,
проверка модели, whisper-cli, компиляция bridge, native smoke, сборка DLL,
проверка экспортов dumpbin) не могут быть воспроизведены агентом локально на macOS.

## CURRENT WINDOWS CI AUTOMATION

На `windows-latest` автоматизированы:

- Windows build закреплённого whisper.cpp и bridge;
- точный набор из 10 экспортов DLL;
- сборка managed interop и smoke;
- runtime availability;
- точные managed-значения статусов и native model-free контракт
  `ModelLoadFailed`.

Реальная inference-проверка с большой моделью остаётся локальным/manual smoke и
не скачивается в CI.
