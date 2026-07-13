using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Nhom2Service.Services;

public sealed class HrCoreClient(IHttpClientFactory factory, IHttpContextAccessor accessor)
{
    public async Task<(bool matched, double? distance)> VerifyFaceAsync(
        double[] descriptor,
        string modelVersion,
        CancellationToken ct)
    {
        var client = factory.CreateClient("HRCore");
        var token = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(token);

        var response = await client.PostAsJsonAsync(
            "api/face-profiles/verify",
            new { descriptor, modelVersion },
            ct);
        if (!response.IsSuccessStatusCode) return (false, null);

        var data = await response.Content.ReadFromJsonAsync<FaceResult>(cancellationToken: ct);
        return (data?.Matched == true, data?.Distance);
    }

    private sealed record FaceResult(bool Matched, double Distance);
}
