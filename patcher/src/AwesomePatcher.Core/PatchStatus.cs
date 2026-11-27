namespace AwesomePatcher.Core
{
    public sealed class PatchStatus
    {
        internal PatchStatus(string message, bool canPatch = false, bool canRevert = false, bool isCurrent = false,
            bool filesOriginal = false, bool interrupted = false)
        {
            FilesOriginal = filesOriginal;
            Interrupted = interrupted;
            Message = message;
            CanPatch = canPatch;
            CanRevert = canRevert;
            IsCurrent = isCurrent;
        }

        internal bool FilesOriginal { get; }
        internal bool Interrupted { get; }
        public string Message { get; }
        public bool CanPatch { get; }
        public bool CanRevert { get; }
        public bool IsCurrent { get; }
    }
}
