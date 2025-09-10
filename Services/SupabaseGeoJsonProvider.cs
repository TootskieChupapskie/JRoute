using System.Text;
// Alias namespaces to avoid 'Client' ambiguity
using Sb = Supabase;
using SbStorage = Supabase.Storage;

public interface IGeoJsonProvider
{
    Task<string> GetAsync(string bucket, string path);
}

public sealed class SupabaseGeoJsonProvider : IGeoJsonProvider
{
    private readonly Sb.Client _sb;                 // <-- Supabase.Client
    public SupabaseGeoJsonProvider(Sb.Client sb)    // <-- Supabase.Client
    {
        _sb = sb;
    }

    public async Task<string> GetAsync(string bucket, string path)
    {
        await _sb.InitializeAsync();

        // Use typed nulls to select the 3-arg overload and avoid ambiguity
        var bytes = await _sb.Storage.From(bucket)
            .Download(path, (SbStorage.TransformOptions?)null, (EventHandler<float>?)null);

        return Encoding.UTF8.GetString(bytes);
    }
}
