using Microsoft.Win32.SafeHandles;

namespace PolinMegatranscriber.Native;

internal sealed class SafeWhisperSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeWhisperSessionHandle(nint session)
        : base(ownsHandle: true)
    {
        SetHandle(session);
    }

    protected override bool ReleaseHandle()
    {
        PmtWhisperNative.PmtWhisperSessionDestroy(handle);
        return true;
    }
}
