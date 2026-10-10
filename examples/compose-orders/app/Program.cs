using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var connectionString = builder.Configuration["DATABASE_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("DATABASE_CONNECTION_STRING is required.");

async Task<IResult> ReadyAsync(CancellationToken ct)
{
    try
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS orders (order_id text PRIMARY KEY, status text NOT NULL)", connection);
        await command.ExecuteNonQueryAsync(ct);
        return Results.Ok(new { ready = true });
    }
    catch (NpgsqlException) { return Results.StatusCode(503); }
}

app.MapGet("/", ReadyAsync);
app.MapGet("/health", ReadyAsync);
app.MapPost("/orders", async (Order order, CancellationToken ct) =>
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(ct);
    await using var command = new NpgsqlCommand("INSERT INTO orders (order_id, status) VALUES (@id, 'CREATED')", connection);
    command.Parameters.AddWithValue("id", order.Id);
    await command.ExecuteNonQueryAsync(ct);
    return Results.Created("/orders/" + Uri.EscapeDataString(order.Id), new { id = order.Id, status = "CREATED" });
});
await app.RunAsync();

internal sealed record Order(string Id);
