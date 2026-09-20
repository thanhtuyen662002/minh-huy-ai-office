using Microsoft.EntityFrameworkCore;
using Xunit;
namespace MinhHuy.AIOffice.Platform.Persistence.Tests;
public sealed class DurableTaskModelTests
{
    [Fact]
    public void Model_UsesTenantCompanyTaskScopedKeysAndRelationships()
    {
        using var context = CreateContext();
        var task=context.Model.FindEntityType(typeof(TaskRecord)); var step=context.Model.FindEntityType(typeof(TaskStepRecord)); var dependency=context.Model.FindEntityType(typeof(TaskDependencyRecord)); var taskEvent=context.Model.FindEntityType(typeof(TaskEventRecord)); var checkpoint=context.Model.FindEntityType(typeof(TaskCheckpointRecord));
        Assert.NotNull(task); Assert.NotNull(step); Assert.NotNull(dependency); Assert.NotNull(taskEvent); Assert.NotNull(checkpoint);
        Assert.Equal(new[]{nameof(TaskRecord.TenantId),nameof(TaskRecord.CompanyId),nameof(TaskRecord.Id)},task.FindPrimaryKey()!.Properties.Select(p=>p.Name));
        Assert.Equal(new[]{nameof(TaskStepRecord.TenantId),nameof(TaskStepRecord.CompanyId),nameof(TaskStepRecord.TaskId),nameof(TaskStepRecord.Id)},step.FindPrimaryKey()!.Properties.Select(p=>p.Name));
        Assert.Equal(new[]{nameof(TaskDependencyRecord.TenantId),nameof(TaskDependencyRecord.CompanyId),nameof(TaskDependencyRecord.TaskId),nameof(TaskDependencyRecord.StepId),nameof(TaskDependencyRecord.DependsOnStepId)},dependency.FindPrimaryKey()!.Properties.Select(p=>p.Name));
        Assert.Equal(new[]{nameof(TaskEventRecord.TenantId),nameof(TaskEventRecord.CompanyId),nameof(TaskEventRecord.TaskId),nameof(TaskEventRecord.Sequence)},taskEvent.FindPrimaryKey()!.Properties.Select(p=>p.Name));
        Assert.Equal(new[]{nameof(TaskCheckpointRecord.TenantId),nameof(TaskCheckpointRecord.CompanyId),nameof(TaskCheckpointRecord.TaskId),nameof(TaskCheckpointRecord.StepId),nameof(TaskCheckpointRecord.Version)},checkpoint.FindPrimaryKey()!.Properties.Select(p=>p.Name));
        Assert.Contains(task.GetForeignKeys(),fk=>fk.PrincipalEntityType.ClrType==typeof(CompanyMembershipRecord)&&fk.Properties.Select(p=>p.Name).SequenceEqual(new[]{nameof(TaskRecord.TenantId),nameof(TaskRecord.CompanyId),nameof(TaskRecord.CreatedByUserId)}));
        Assert.Contains(step.GetForeignKeys(),fk=>fk.PrincipalEntityType.ClrType==typeof(TaskRecord)&&fk.Properties.Select(p=>p.Name).SequenceEqual(new[]{nameof(TaskStepRecord.TenantId),nameof(TaskStepRecord.CompanyId),nameof(TaskStepRecord.TaskId)}));
        Assert.Equal(2,dependency.GetForeignKeys().Count(fk=>fk.PrincipalEntityType.ClrType==typeof(TaskStepRecord)));
        Assert.Contains(checkpoint.GetForeignKeys(),fk=>fk.PrincipalEntityType.ClrType==typeof(TaskStepRecord)&&fk.Properties.Select(p=>p.Name).SequenceEqual(new[]{nameof(TaskCheckpointRecord.TenantId),nameof(TaskCheckpointRecord.CompanyId),nameof(TaskCheckpointRecord.TaskId),nameof(TaskCheckpointRecord.StepId)}));
        Assert.Equal(40,task.FindProperty(nameof(TaskRecord.Status))?.GetMaxLength()); Assert.Equal(40,step.FindProperty(nameof(TaskStepRecord.Status))?.GetMaxLength()); Assert.Equal(100,taskEvent.FindProperty(nameof(TaskEventRecord.EventType))?.GetMaxLength());
    }
    private static PlatformDbContext CreateContext()=>new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer("Server=localhost;Database=AIOffice_Model_Test;User Id=test;Password=test;TrustServerCertificate=true").Options);
}
