# Known risks

## WIN-RISK-001 — Non-ASCII Windows paths

**Observed:**
raw whisper-cli v1.9.1 аварийно завершился при использовании путей, передаваемых через Windows user profile с кириллическими / non-ASCII символами.

**Example environment:**
`C:\Users\Козявочка\...`

**Control:**
тот же runtime / модель / образец успешно обработаны при доступе через ASCII virtual drive `W:`.

**Interpretation:**
known Windows non-ASCII path compatibility risk.

**Unknown:**
точный root cause не установлен.

**Do NOT claim:**
- что все Unicode-пути всегда падают;
- что PolinWhisperBridge сам по себе доказанно сломан на всех Unicode-путях;
- что место исправления уже известно.

**Future requirement:**
отдельная regression investigation и автоматизированный тест до релиза.

**Status:**
OPEN / NOT FIXED / DO NOT SOLVE IN BOOTSTRAP.
