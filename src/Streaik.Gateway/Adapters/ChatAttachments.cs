using Streaik.Messages;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

internal static class ChatAttachments
{
    public static bool IsImage(string mediaType) => mediaType.StartsWith("image/", StringComparison.Ordinal);

    // Validate the input shape; never resolve or download an attached URL in the gateway.
    public static bool IsLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0;

    public static string LinkText(string url) => $"\n\nLink: {url}";

    public static string? InlineData(string url, string mediaType)
    {
        var prefix = $"data:{mediaType};base64,";
        return mediaType.Length > 0 && mediaType.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '+' or '-' or '_')
            && url.StartsWith(prefix, StringComparison.Ordinal) ? url[prefix.Length..] : null;
    }

    public static ContractException Unsupported() => new("This route does not support one of the attached file types. Use a compatible route or convert text files to text.", 422);

    public static MessageContentBlock ToMessages(FileUIPart file, bool viaChat, bool documentUrls = false)
    {
        if (file.ProviderReference is { Count: > 0 }) throw Unsupported();
        var image = IsImage(file.MediaType);
        var pdf = file.MediaType == "application/pdf";
        if (IsLink(file.Url))
            return image || documentUrls || (pdf && !viaChat)
                ? new() { Type = image ? "image" : "document", Source = new() { Type = "url", Url = file.Url }, Title = image ? null : file.Filename }
                : new() { Type = "text", Text = LinkText(file.Url) };

        var data = InlineData(file.Url, file.MediaType) ?? throw Unsupported();
        if (image || pdf) return new() { Type = image ? "image" : "document", Title = pdf ? file.Filename : null,
            Source = new() { Type = "base64", MediaType = file.MediaType, Data = data } };
        if (file.MediaType != "text/plain" || viaChat) throw Unsupported();
        try
        {
            return new() { Type = "document", Title = file.Filename, Source = new()
                { Type = "text", MediaType = "text/plain", Data = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data)) } };
        }
        catch (FormatException) { throw Unsupported(); }
    }
}
