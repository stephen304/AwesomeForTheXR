namespace AwesomePatcher.Core
{
    public sealed class PatchStatus
    {
        internal PatchStatus(string message, bool canPatch = false, bool canRevert = false, bool isCurrent = false)
        {
            Message = message;
            CanPatch = canPatch;
            CanRevert = canRevert;
            IsCurrent = isCurrent;
        }

        public string Message { get; }
        public bool CanPatch { get; }
        public bool CanRevert { get; }
        public bool IsCurrent { get; }
    }
}
