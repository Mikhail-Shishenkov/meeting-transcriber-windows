# AGENTS.md — meeting-transcriber-windows (project-specific rules)

Глобальные правила работы (commit/push, scope, privacy) задаются global AGENTS.md и не дублируются здесь.

## Behavioral reference

1. `meeting-transcriber-mac` — behavioral reference implementation этого проекта.
2. Windows-порт сохраняет пользовательское поведение Mac-версии, если отдельная Windows-спецификация явно не говорит иначе.

## Архитектура

3. Базовая связь слоёв:

   ```
   WPF / C# -> P/Invoke -> pmtwhisper.dll (PolinWhisperBridge) -> whisper.cpp
   ```

4. whisper.cpp закреплён:
   - tag: `v1.9.1`
   - commit: `f049fff95a089aa9969deb009cdd4892b3e74916`

## Ограничения MVP

5. CPU — обязательный базовый режим работы.
6. CUDA/GPU acceleration не входит в MVP.
7. C API PolinWhisperBridge не изменяется без отдельной задачи.
8. Продуктовая семантика progress / cancellation / timestamps / model handling не изменяется без отдельной задачи.
9. macOS-репозиторий не копируется; он используется только как reference.
10. Windows-specific поведение проверяется через CI на `windows-latest` и/или real Windows validation.
11. Known non-ASCII path issue (WIN-RISK-001) — отдельный investigation/regression item; root cause не считать установленным.
12. Cloud transcription, accounts, telemetry, auto-update и сложность installer не добавляются в MVP без отдельной задачи.
