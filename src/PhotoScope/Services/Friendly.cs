using System.Runtime.InteropServices;

namespace PhotoScope.Services;

/// <summary>Turns exceptions into short messages suitable for the UI (no stack traces).</summary>
public static class Friendly
{
    const int WincodecErrComponentNotFound = unchecked((int)0x88982F50);
    const int WincodecErrBadHeader = unchecked((int)0x88982F61);
    const int WincodecErrBadImage = unchecked((int)0x88982F60);
    const int WincodecErrBadStreamData = unchecked((int)0x88982F62);
    const int WincodecErrUnknownImageFormat = unchecked((int)0x88982F07);
    const int WincodecErrStreamRead = unchecked((int)0x88982F72);
    const int MfTopoCodecNotFound = unchecked((int)0xC00D5212);
    const int ErrorSharingViolation = unchecked((int)0x80070020);
    const int ErrorLockViolation = unchecked((int)0x80070021);
    const int ErrorDiskFull = unchecked((int)0x80070070);
    const int ErrorCloudFile = unchecked((int)0x8007016A); // ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING

    static readonly HashSet<string> BuiltInFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico", ".jxr", ".wdp",
    };

    /// <summary>Like <see cref="Describe"/>, but knows which formats Windows always decodes.</summary>
    public static string DescribeImageError(Exception ex, string path)
    {
        var message = Describe(ex);
        if (message != CodecMissing) return message;
        var ext = Path.GetExtension(path);
        if (BuiltInFormats.Contains(ext)) return "The file is damaged or isn't a valid image.";
        return ext.ToLowerInvariant() switch
        {
            ".webp" => "WebP needs the WebP Image Extensions from the Microsoft Store.",
            ".heic" or ".heif" => "HEIC needs the HEIF Image Extensions (and HEVC Video Extensions) from the Microsoft Store.",
            ".avif" => "AVIF needs the AV1 Video Extension from the Microsoft Store.",
            _ => message,
        };
    }

    const string CodecMissing = "No Windows image codec is installed for this format.";

    /// <summary>True for messages that installing a codec could fix, so they're worth retrying later.</summary>
    public static bool IsCodecMessage(string message) =>
        message == CodecMissing || message.Contains("Microsoft Store", StringComparison.Ordinal);

    /// <summary>
    /// True when decoding will fail the same way every time (bad data or missing codec), as opposed
    /// to temporary problems such as a locked file, an offline drive or low memory.
    /// </summary>
    public static bool IsPermanentImageError(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e.HResult)
            {
                case WincodecErrBadHeader or WincodecErrBadImage or WincodecErrBadStreamData or WincodecErrComponentNotFound
                    or WincodecErrUnknownImageFormat or MfTopoCodecNotFound:
                    return true;
                case ErrorSharingViolation or ErrorLockViolation or ErrorCloudFile or ErrorDiskFull:
                    return false;
            }
        }
        return ex switch
        {
            OutOfMemoryException or InsufficientMemoryException or OperationCanceledException => false,
            UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => false,
            FileFormatException or ImageTooLargeException or NotSupportedException or ArgumentException or OverflowException => true,
            IOException => false,
            _ => true,
        };
    }

    public static string Describe(Exception ex)
    {
        // Imaging errors are often wrapped, so look at the whole chain for a known HRESULT.
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e.HResult)
            {
                case MfTopoCodecNotFound:
                    return "This HEIC/HEIF image needs the HEVC Video Extensions from the Microsoft Store.";
                case WincodecErrComponentNotFound:
                case WincodecErrUnknownImageFormat:
                    return CodecMissing;
                case WincodecErrBadHeader:
                case WincodecErrBadImage:
                case WincodecErrBadStreamData:
                case WincodecErrStreamRead:
                    return "The file is damaged or isn't a valid image.";
                case ErrorSharingViolation:
                case ErrorLockViolation:
                    return "The file is in use by another program.";
                case ErrorDiskFull:
                    return "The disk is full.";
                case ErrorCloudFile:
                    return "The cloud storage provider for this file isn't running.";
            }
        }

        return ex switch
        {
            UserFacingException u => u.Message,
            NetworkTimeoutException => "The network location isn't responding. PhotoScope will try again later.",
            FileNotFoundException or DirectoryNotFoundException => "The file could not be found.",
            DriveNotFoundException => "The drive is not available.",
            UnauthorizedAccessException => "Access to the file was denied.",
            PathTooLongException => "The file path is too long.",
            FileFormatException => "The file is damaged or isn't a valid image.",
            OutOfMemoryException or InsufficientMemoryException => "The image is too large to open.",
            ImageTooLargeException tooLarge => tooLarge.Message,
            NotSupportedException when ex.Message.Contains("imaging component", StringComparison.OrdinalIgnoreCase)
                => CodecMissing,
            NotSupportedException => "This format isn't supported.",
            OverflowException or ArgumentException or InvalidOperationException => "The file is damaged or isn't a valid image.",
            COMException => "Windows couldn't decode this image.",
            IOException io => "The file could not be read. " + io.Message,
            OperationCanceledException => "The operation was cancelled.",
            _ => "Something went wrong. Details were written to the log."
        };
    }
}

/// <summary>Thrown when an image is too big to decode safely.</summary>
public sealed class ImageTooLargeException(string message) : Exception(message);
