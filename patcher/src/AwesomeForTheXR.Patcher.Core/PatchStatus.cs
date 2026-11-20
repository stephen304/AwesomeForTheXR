namespace AwesomeForTheXR.Patcher.Core
{
    public sealed class PatchStatus
    {
        internal PatchStatus(string message, bool canPatch = false, bool canRevert = false)
        {
            Message = message;
            CanPatch = canPatch;
            CanRevert = canRevert;
        }

        public string Message { get; }
        public bool CanPatch { get; }
        public bool CanRevert { get; }
    }
}
