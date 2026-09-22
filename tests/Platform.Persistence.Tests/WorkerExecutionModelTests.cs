using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class WorkerExecutionModelTests
{
    [Fact]
    public void Model_EnforcesTenantScopedExecutionLeaseAndDispatchOutbox()
    {
        using var context = CreateContext();

        var execution = context.Model.FindEntityType(typeof(TaskStepExecutionRecord));
        var dispatch = context.Model.FindEntityType(typeof(TaskDispatchRecord));

        Assert.NotNull(execution);
        Assert.NotNull(dispatch);

        Assert.Equal(
            new[]
            {
                nameof(TaskStepExecutionRecord.TenantId),
                nameof(TaskStepExecutionRecord.CompanyId),
                nameof(TaskStepExecutionRecord.TaskId),
                nameof(TaskStepExecutionRecord.StepId)
            },
            execution.FindPrimaryKey()!.Properties.Select(property => property.Name));

        Assert.Contains(
            execution.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(TaskStepRecord)
                && foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(TaskStepExecutionRecord.TenantId),
                            nameof(TaskStepExecutionRecord.CompanyId),
                            nameof(TaskStepExecutionRecord.TaskId),
                            nameof(TaskStepExecutionRecord.StepId)
                        }));

        var rowVersion = execution.FindProperty(nameof(TaskStepExecutionRecord.RowVersion));
        Assert.NotNull(rowVersion);
        Assert.True(rowVersion.IsConcurrencyToken);

        Assert.Contains(
            execution.GetIndexes(),
            index =>
                index.IsUnique
                && index.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(TaskStepExecutionRecord.TenantId),
                            nameof(TaskStepExecutionRecord.CompanyId),
                            nameof(TaskStepExecutionRecord.IdempotencyKey)
                        }));

        Assert.Equal(
            new[]
            {
                nameof(TaskDispatchRecord.TenantId),
                nameof(TaskDispatchRecord.CompanyId),
                nameof(TaskDispatchRecord.TaskId),
                nameof(TaskDispatchRecord.StepId),
                nameof(TaskDispatchRecord.MessageId)
            },
            dispatch.FindPrimaryKey()!.Properties.Select(property => property.Name));

        Assert.Contains(
            dispatch.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(TaskStepRecord)
                && foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(TaskDispatchRecord.TenantId),
                            nameof(TaskDispatchRecord.CompanyId),
                            nameof(TaskDispatchRecord.TaskId),
                            nameof(TaskDispatchRecord.StepId)
                        }));

        Assert.Contains(
            dispatch.GetIndexes(),
            index =>
                index.IsUnique
                && index.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(TaskDispatchRecord.TenantId),
                            nameof(TaskDispatchRecord.CompanyId),
                            nameof(TaskDispatchRecord.TaskId),
                            nameof(TaskDispatchRecord.StepId),
                            nameof(TaskDispatchRecord.Attempt)
                        }));
    }

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(
                "Server=localhost;Database=AIOffice_Model_Test;User Id=test;Password=test;TrustServerCertificate=true")
            .Options;

        return new PlatformDbContext(options);
    }
}
