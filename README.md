# Мегатранскрибатор (meeting-transcriber-windows)

Windows-порт рабочего эталонного macOS-приложения «Мегатранскрибатор».
Цель — полноценный Windows-аналог с теми же пользовательскими
сценариями, поведением и аналогичным UI/UX, а не новый продукт по мотивам
Mac-версии. Финальная поставка — обычное Windows-приложение с `.exe`-установщиком.

- Reference repository: <https://github.com/Mikhail-Shishenkov/meeting-transcriber-mac>
- Целевая платформа: Windows 10/11 x64
- Стек: C# / .NET 10 / WPF
- Распознавание речи: собственный C-compatible bridge `pmtwhisper.dll` (PolinWhisperBridge) поверх whisper.cpp
- Режим работы: CPU-first (GPU/CUDA не входит в MVP)

## Текущий статус

Собран и проверяется в Windows CI закреплённый CPU-only `whisper.cpp`,
`pmtwhisper.dll` с точным набором из 10 экспортов и managed .NET-слой. Managed API
уже владеет native session, транскрибирует WAV, читает UTF-8 сегменты,
язык и timestamps, а также поддерживает progress и cancellation. WPF UI, ffmpeg,
менеджер моделей и установщик ещё не реализованы.
Над native-слоем есть UI-independent async Core-сервис с
`CancellationToken`, монотонным progress, domain errors и защитой от
параллельного inference. Unicode Windows paths с кириллицей и пробелами
поддерживаются bridge-слоем и закреплены model-free regression-тестом.

Spike-факты по Windows-окружению зафиксированы в [docs/SPIKE_RESULTS.md](docs/SPIKE_RESULTS.md),
известные риски — в [docs/KNOWN_RISKS.md](docs/KNOWN_RISKS.md).
