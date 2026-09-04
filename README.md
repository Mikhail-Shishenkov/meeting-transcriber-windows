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

Собраны native bridge, managed Core и первый запускаемый WPF-клиент. Приложение
поддерживает три продуктовых режима, асинхронную проверку медиа, Small/Medium
модели, загрузку модели с progress, реальную отмену и публикацию MP3/TXT/SRT.
Unicode Windows paths с кириллицей и пробелами поддерживаются всем pipeline.

До bundled release FFmpeg ищется в `Runtime\MediaTools` рядом с приложением,
в каталоге из `POLIN_MEGATRANSCRIBER_MEDIA_TOOLS`, в `PATH`, затем в локальном
`C:\ffmpeg`. Установщик и поставка FFmpeg будут добавлены отдельным этапом.

Release-версия после сборки запускается командой:

```powershell
& .\managed\PolinMegatranscriber.App\bin\Release\net10.0-windows\PolinMegatranscriber.App.exe
```

Spike-факты по Windows-окружению зафиксированы в [docs/SPIKE_RESULTS.md](docs/SPIKE_RESULTS.md),
известные риски — в [docs/KNOWN_RISKS.md](docs/KNOWN_RISKS.md).
