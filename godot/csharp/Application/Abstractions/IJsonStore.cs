using System.Text.Json.Nodes;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IJsonStore
{
    JsonObject? Read(string path);
    bool Write(string path, JsonObject document);
    bool Exists(string path);
}
