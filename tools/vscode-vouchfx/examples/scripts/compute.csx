// Referenced by ../script-csharp-external-file.e2e.yaml via `file:`.
// Spliced verbatim into the compiled submission at compile time — the same
// body could equally have been written inline as `code:` in the YAML.
var id = Vars["greeting"]?.ToString() ?? "world";
var count = 0;
for (var i = 0; i < 3; i++)
{
    count += i;
}
Vars["message"] = $"{id} ({count})";
Console.WriteLine(Vars["message"]);
