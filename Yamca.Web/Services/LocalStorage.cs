using Microsoft.JSInterop;

namespace Yamca.Web.Services;

public sealed class LocalStorage
{
    private readonly IJSRuntime _js;

    public LocalStorage(IJSRuntime js) => _js = js;

    public ValueTask<string?> GetItemAsync(string key) =>
        _js.InvokeAsync<string?>("yamcaStorage.getItem", key);

    public ValueTask<bool> SetItemAsync(string key, string value) =>
        _js.InvokeAsync<bool>("yamcaStorage.setItem", key, value);

    public ValueTask<bool> RemoveItemAsync(string key) =>
        _js.InvokeAsync<bool>("yamcaStorage.removeItem", key);
}
