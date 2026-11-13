using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>
/// The earlier turns of the conversation a question belongs to, so the model understands follow-ups such as
/// "còn tháng trước thì sao?". Only the asker's own completed, not-deleted questions in the same company are read;
/// each turn is bounded so a long conversation cannot crowd out the database context.
/// </summary>
public static class ConversationContext
{
    public const int MaximumTurns = 6;
    public const int MaximumQuestion = 800;
    public const int MaximumAnswer = 1500;
    public const int MaximumQueriesPerTurn = 3;
    public const int MaximumQuery = 600;

    public static async Task<string?> ReadAsync(PlatformDbContext database, WorkDispatchEnvelope envelope, CancellationToken token)
    {
        var current = await database.Tasks.AsNoTracking()
            .Where(task => task.TenantId == envelope.TenantId && task.CompanyId == envelope.CompanyId && task.Id == envelope.TaskId)
            .Select(task => new { task.ConversationId, task.CreatedByUserId, task.CreatedAtUtc, task.ExternalUserId })
            .SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (current?.ConversationId is not { } conversation) return null;

        var hidden = database.HiddenTasks.AsNoTracking();
        var earlier = await database.Tasks.AsNoTracking()
            .Where(task => task.TenantId == envelope.TenantId && task.CompanyId == envelope.CompanyId
                && task.CreatedByUserId == current.CreatedByUserId && task.ExternalUserId == current.ExternalUserId && task.Id != envelope.TaskId
                && (task.ConversationId == conversation || task.Id == conversation)
                && task.CreatedAtUtc <= current.CreatedAtUtc && task.Status == TaskExecutionStatus.Completed
                && !hidden.Any(row => row.TenantId == task.TenantId && row.CompanyId == task.CompanyId && row.TaskId == task.Id))
            .OrderByDescending(task => task.CreatedAtUtc).Take(MaximumTurns)
            .Select(task => new { task.Id, task.CreatedAtUtc })
            .ToListAsync(token).ConfigureAwait(false);
        if (earlier.Count == 0) return null;

        var ids = earlier.Select(task => task.Id).ToArray();
        var requests = await database.TaskEvents.AsNoTracking()
            .Where(row => row.TenantId == envelope.TenantId && row.CompanyId == envelope.CompanyId && ids.Contains(row.TaskId)
                && row.EventType == PilotTaskRequestEvent.EventType)
            .Select(row => new { row.TaskId, row.PayloadJson }).ToListAsync(token).ConfigureAwait(false);
        var checkpoints = await database.TaskCheckpoints.AsNoTracking()
            .Where(row => row.TenantId == envelope.TenantId && row.CompanyId == envelope.CompanyId && ids.Contains(row.TaskId))
            .Select(row => new { row.TaskId, row.Version, row.PayloadJson }).ToListAsync(token).ConfigureAwait(false);

        var turns = earlier.OrderBy(task => task.CreatedAtUtc).Select(task => new Turn(
            Question(requests.Where(row => row.TaskId == task.Id).Select(row => row.PayloadJson).FirstOrDefault()),
            checkpoints.Where(row => row.TaskId == task.Id).OrderByDescending(row => row.Version).Select(row => row.PayloadJson).FirstOrDefault()))
            .Where(turn => turn.Question is not null)
            .ToArray();
        return turns.Length == 0 ? null : Format(turns);
    }

    public sealed record Turn(string? Question, string? CheckpointJson);

    public static string Format(IReadOnlyList<Turn> turns)
    {
        var text = new StringBuilder();
        for (var index = 0; index < turns.Count; index++)
        {
            var (answer, queries) = Answer(turns[index].CheckpointJson);
            text.Append("[Q").Append(index + 1).Append("] ").AppendLine(Bounded(turns[index].Question!, MaximumQuestion));
            text.Append("[A").Append(index + 1).Append("] ").AppendLine(answer is null ? "(no saved answer)" : Bounded(answer, MaximumAnswer));
            foreach (var sql in queries) text.Append("    SQL used: ").AppendLine(Bounded(OneLine(sql), MaximumQuery));
        }
        return text.ToString().TrimEnd();
    }

    private static string? Question(string? payload)
    {
        if (payload is null) return null;
        try
        {
            var question = JsonSerializer.Deserialize<PilotTaskRequestEvent>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Question;
            if (string.IsNullOrWhiteSpace(question)) return null;
            var (text, pictures) = AssistantImageTokens.Parse(question);
            return pictures.Count == 0 ? text : $"{(text.Length == 0 ? "" : text + " ")}({pictures.Count} ảnh đính kèm)";
        }
        catch (JsonException) { return null; }
    }

    private static (string? Answer, IReadOnlyList<string> Queries) Answer(string? payload)
    {
        if (payload is null) return (null, []);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, []);
            var answer = root.TryGetProperty("answer", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var queries = new List<string>();
            if (root.TryGetProperty("erpQueries", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var query in list.EnumerateArray())
                {
                    if (query.ValueKind != JsonValueKind.Object || queries.Count >= MaximumQueriesPerTurn) continue;
                    var failed = query.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String;
                    if (!failed && query.TryGetProperty("sql", out var sql) && sql.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(sql.GetString())) queries.Add(sql.GetString()!);
                }
            return (answer, queries);
        }
        catch (JsonException) { return (null, []); }
    }

    private static string OneLine(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Bounded(string value, int length)
    {
        var text = value.Trim();
        return text.Length <= length ? text : text[..length] + "…";
    }
}
