using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record AssistantImageReceipt(Guid Id, string ContentType, int Bytes);
public sealed record AssistantImage(string ContentType, byte[] Content);
public sealed class AssistantImageNotFoundException() : Exception("Assistant picture is unavailable.");

/// <summary>
/// Pictures for Trợ lý questions and deleting questions from the asker's history. Everything is scoped to
/// the active member: a picture is readable only by its uploader, and only the asker can hide a question.
/// </summary>
public sealed class AssistantService(PlatformDbContext database, IAuthorizationDirectory directory, TimeProvider? clock = null)
{
    public const int MaximumImageBytes = 5 * 1024 * 1024;
    // A generous daily cap per member so a stuck client cannot fill the database.
    public const int MaximumImagesPerDay = 500;
    public const int MaximumHiddenPerRequest = 100;
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public async Task<AssistantImageReceipt> UploadAsync(AuthorizationContext authority, string? declaredType, byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is < 12 or > MaximumImageBytes) throw new ArgumentException("Picture size is invalid.");
        var type = BotChannelJson.ImageType(content);
        if (type is null || !string.Equals(type, declaredType, StringComparison.Ordinal)) throw new ArgumentException("Picture type is invalid.");
        await RequireAsync(authority, cancellationToken);
        var now = time.GetUtcNow();
        var today = await database.AssistantImages.AsNoTracking().CountAsync(x => x.TenantId == authority.TenantId
            && x.CompanyId == authority.CompanyId && x.UploadedByUserId == authority.UserId && x.CreatedAtUtc > now.AddDays(-1), cancellationToken);
        if (today >= MaximumImagesPerDay) throw new InvalidOperationException("Daily picture limit reached.");
        var row = new AssistantImageRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Id = Guid.NewGuid(),
            UploadedByUserId = authority.UserId,
            ContentType = type,
            Content = content,
            CreatedAtUtc = now
        };
        database.AssistantImages.Add(row);
        await database.SaveChangesAsync(cancellationToken);
        return new(row.Id, type, content.Length);
    }

    public async Task<AssistantImage> ImageAsync(AuthorizationContext authority, Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Picture identity is required.");
        await RequireAsync(authority, cancellationToken);
        return await database.AssistantImages.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId
                && x.Id == id && x.UploadedByUserId == authority.UserId)
            .Select(x => new AssistantImage(x.ContentType, x.Content)).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AssistantImageNotFoundException();
    }

    /// <summary>Pictures named by a task's question, read for the task's asker; unknown or foreign ids are skipped.</summary>
    public static async Task<IReadOnlyList<AssistantImage>> ForTaskAsync(PlatformDbContext database, Guid tenantId, Guid companyId,
        Guid askerUserId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return [];
        var wanted = ids.Take(AssistantImageTokens.MaximumImages).ToArray();
        var rows = await database.AssistantImages.AsNoTracking().Where(x => x.TenantId == tenantId && x.CompanyId == companyId
                && x.UploadedByUserId == askerUserId && wanted.Contains(x.Id))
            .Select(x => new { x.Id, x.ContentType, x.Content }).ToArrayAsync(cancellationToken);
        return wanted.Select(id => rows.FirstOrDefault(row => row.Id == id)).Where(row => row is not null)
            .Select(row => new AssistantImage(row!.ContentType, row.Content)).ToArray();
    }

    /// <summary>Removes the asker's questions from their history and deletes the pictures those questions carried.</summary>
    public async Task<int> HideTasksAsync(AuthorizationContext authority, IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var ids = taskIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length is 0 or > MaximumHiddenPerRequest || ids.Length != taskIds.Count) throw new ArgumentException("Invalid question list.");
        await RequireAsync(authority, cancellationToken);
        var owned = await database.Tasks.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId
            && x.CreatedByUserId == authority.UserId && ids.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(cancellationToken);
        var already = await database.HiddenTasks.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId
            && owned.Contains(x.TaskId)).Select(x => x.TaskId).ToArrayAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var id in owned.Except(already))
            database.HiddenTasks.Add(new() { TenantId = authority.TenantId, CompanyId = authority.CompanyId, TaskId = id, UserId = authority.UserId, HiddenAtUtc = now });

        var payloads = await database.TaskEvents.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId
            && owned.Contains(x.TaskId) && x.Sequence == 1 && x.EventType == PilotTaskRequestEvent.EventType)
            .Select(x => x.PayloadJson).ToArrayAsync(cancellationToken);
        var pictures = payloads.Select(TaskHistoryMetadata.Request).Where(request => request is not null)
            .SelectMany(request => AssistantImageTokens.Parse(request!.Question).ImageIds).Distinct().ToArray();
        if (pictures.Length > 0)
        {
            var rows = await database.AssistantImages.Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId
                && x.UploadedByUserId == authority.UserId && pictures.Contains(x.Id)).ToArrayAsync(cancellationToken);
            database.AssistantImages.RemoveRange(rows);
        }
        await database.SaveChangesAsync(cancellationToken);
        return owned.Length;
    }

    private async Task RequireAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var current = await directory.ResolveAsync(authority, cancellationToken);
        if (current?.Context != authority) throw new UnauthorizedAccessException("An active company membership is required.");
    }
}
