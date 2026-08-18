namespace MoveImageForm.Services
{
    /// <summary>单文件上传结果：区分成功 / 跳过 / 失败，并附带原因。</summary>
    public sealed class TransferUploadResult
    {
        public bool Success { get; private set; }
        public bool Skipped { get; private set; }
        public bool Failed { get; private set; }
        public string Detail { get; private set; }

        public static TransferUploadResult Ok()
        {
            return new TransferUploadResult { Success = true, Detail = "" };
        }

        public static TransferUploadResult Skip(string detail)
        {
            return new TransferUploadResult { Skipped = true, Detail = detail ?? "已跳过" };
        }

        public static TransferUploadResult Fail(string detail)
        {
            return new TransferUploadResult { Failed = true, Detail = detail ?? "上传失败" };
        }
    }
}
