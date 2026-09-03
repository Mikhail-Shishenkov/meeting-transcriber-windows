# Мегатранскрибатор (meeting-transcriber-windows)

Windows-порт рабочего эталонного macOS-приложения «Мегатранскрибатор».

- Reference repository: <https://github.com/Mikhail-Shishenkov/meeting-transcriber-mac>
- Целевая платформа: Windows 10/11 x64
- Стек: C# / .NET 10 / WPF
- Распознавание речи: собственный C-compatible bridge `pmtwhisper.dll` (PolinWhisperBridge) поверх whisper.cpp
- Режим работы: CPU-first (GPU/CUDA не входит в MVP)

## Текущий статус

Repository bootstrap: базовая документация и правила проекта зафиксированы, реализация не начиналась.
Spike-факты по Windows-окружению зафиксированы в [docs/SPIKE_RESULTS.md](docs/SPIKE_RESULTS.md),
известные риски — в [docs/KNOWN_RISKS.md](docs/KNOWN_RISKS.md).
