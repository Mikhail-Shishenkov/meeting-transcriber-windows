# Known risks

## WIN-RISK-001 — Non-ASCII Windows paths

**Resolved for PolinWhisperBridge:**

Managed strings reached the unchanged C API as UTF-8. Model loading already
worked because whisper.cpp/ggml opens its UTF-8 path through a Windows wide-path
conversion. The bridge WAV reader instead passed those UTF-8 bytes directly to
MSVC `std::ifstream(const char *)`, which uses a narrow Windows path and could not
open the same file.

The bridge now converts its UTF-8 `const char *` to a native
`std::filesystem::path` with `std::filesystem::u8path` before opening the WAV.
No ANSI-code-page or lossy conversion is used; the C API and exact 10 exports are
unchanged.

**Example environment:**
`C:\Users\Козявочка\...`

**Verified on real Windows 10 x64:**

- model-free CTest opens a valid WAV under a temporary path containing Cyrillic
  and spaces;
- `ggml-small.bin` and `jfk.wav` transcribe successfully through the bridge at
  their real `C:\Users\Козявочка\...` paths without `subst`;
- the same assets also transcribe through temporary hard-link paths whose
  directory and filenames contain Cyrillic and spaces.

**Separate raw whisper-cli observation:**

The historical raw `whisper-cli` build still received a visibly mangled argv
path (`C:\Users\���...`) and exited with `0xC0000409`. The bridge fix does not
change or claim to fix raw CLI argument handling; the application does not use
that executable.

**Status:** CLOSED FOR THE APPLICATION BRIDGE / RAW CLI REMAINS OUT OF SCOPE.
