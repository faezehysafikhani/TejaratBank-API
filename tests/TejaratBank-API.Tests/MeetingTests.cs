using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexus.TaskManagement.Application;
using Nexus.TaskManagement.Application.Dtos;
using Nexus.TaskManagement.Domain;
using Nexus.TaskManagement.Infrastructure;
using NexusCore.Application.Messaging;
using NexusCore.Domain.Identity;
using NexusCore.Infrastructure.Persistence;
using NexusCore.SharedKernel.Interfaces;
using NexusCore.SharedKernel.Results;
using Nexus.Calendar.Application.Meetings;
using Nexus.Calendar.Infrastructure.Meetings;
using Nexus.Calendar.Permissions;
using Nexus.Integrations.CalendarTasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using NexusCore.Application.Identity.Permissions;

namespace TejaratBank_API.Tests;

public class MeetingTests
{
    [Fact]
    public async Task TaskSearch_FindsMyTasksAcrossDates_AndKeepsTenantAndResourceBoundaries()
    {
        using var w=new World();
        var mine=new TaskItem(Guid.NewGuid(),w.Tenant,"پيگيری کار‌ماهانه",new(2024,1,1),TaskPriority.Medium,false,w.Creator);
        var responsible=new TaskItem(Guid.NewGuid(),w.Tenant,"گزارش مسئول",new(2027,1,1),TaskPriority.Medium,false,w.Person);
        responsible.SetResponsibleUsers([w.Creator]);
        var other=new TaskItem(Guid.NewGuid(),w.Tenant,"کار دیگری",new(2026,10,30),TaskPriority.Medium,false,w.Person);
        var foreign=new TaskItem(Guid.NewGuid(),w.OtherTenant,"کار سازمان دیگر",new(2026,10,30),TaskPriority.Medium,false,w.Creator);
        var oldMine=new TaskItem(Guid.NewGuid(),w.Tenant,"کار قدیمی من",new(2025,1,1),TaskPriority.Medium,false) {CreatedByUserId=w.Creator};
        var oldOther=new TaskItem(Guid.NewGuid(),w.Tenant,"کار قدیمی دیگری",new(2025,1,1),TaskPriority.Medium,false) {CreatedByUserId=w.Person};
        w.TasksDb.Tasks.AddRange(mine,responsible,other,foreign,oldMine,oldOther);await w.TasksDb.SaveChangesAsync();
        var result=(await w.Service.SearchTasksAsync(null,1,12,null,default)).Value!;
        Assert.Equal(3,result.TotalCount);Assert.Contains(result.Items,t=>t.Id==mine.Id);Assert.Contains(result.Items,t=>t.Id==responsible.Id);Assert.Contains(result.Items,t=>t.Id==oldMine.Id);
        var search=(await w.Service.SearchTasksAsync("پیگیری کار ماهانه",1,12,null,default)).Value!;
        Assert.Equal(mine.Id,Assert.Single(search.Items).Id);
        var selected=(await w.Service.SearchTasksAsync(null,1,12,string.Join(',',mine.Id,responsible.Id,other.Id,foreign.Id),default)).Value!;
        Assert.Equal(2,selected.Items.Count);
        w.Current.Permissions.Add("Tasks.ManageAll");
        Assert.Equal(3,(await w.Service.SearchTasksAsync(null,1,12,null,default)).Value!.TotalCount);
    }

    [Fact]
    public async Task TaskSearch_PaginatesWithoutLosingOrRepeatingResults()
    {
        using var w=new World();
        for(var i=0;i<13;i++)w.TasksDb.Tasks.Add(new TaskItem(Guid.NewGuid(),w.Tenant,"وظیفه "+i,new(2026,11,1),TaskPriority.Medium,false,w.Creator));
        await w.TasksDb.SaveChangesAsync();
        var first=(await w.Service.SearchTasksAsync(null,1,5,null,default)).Value!;
        var second=(await w.Service.SearchTasksAsync(null,2,5,null,default)).Value!;
        Assert.Equal(13,first.TotalCount);Assert.Equal(5,first.Items.Count);Assert.Equal(5,second.Items.Count);
        Assert.Empty(first.Items.Select(t=>t.Id).Intersect(second.Items.Select(t=>t.Id)));
    }
    [Fact]
    public async Task AllFiveTabs_Persist_AndUpdateWithConcurrencyProtection()
    {
        using var w = new World();
        var input = w.Input() with { Agenda = [new("گزارش ماهانه",15,w.Person)], Minutes = "تصمیمات جلسه",
            Actions = [new("پیگیری گزارش",w.Person,new DateOnly(2026,11,5))] };
        var made = (await w.Service.SaveAsync(null,input,default)).Value!;
        w.Db.ChangeTracker.Clear();
        var saved = Assert.Single((await w.Service.ListAsync(new(2026,10,1),new(2026,11,1),default)).Value!);
        Assert.Equal(w.Creator,saved.CreatedByUserId);Assert.Equal("گزارش ماهانه",Assert.Single(saved.Details.Agenda).Title);
        Assert.Equal("تصمیمات جلسه",saved.Details.Minutes);Assert.Single(saved.Details.Actions);
        var updated = await w.Service.SaveAsync(made.Id,input with {Title="جلسه اصلاح‌شده",Version=made.Version},default);
        Assert.True(updated.IsSuccess);Assert.Equal(2,updated.Value!.Version);
        Assert.Equal("conflict",(await w.Service.SaveAsync(made.Id,input with {Version=made.Version},default)).Error.Code);
    }

    [Fact]
    public async Task ParticipantsCanRead_ButUnrelatedUsersAndOtherTenantsCannot()
    {
        using var w = new World();var made=(await w.Service.SaveAsync(null,w.Input(),default)).Value!;
        w.Current.UserId=w.Person;
        Assert.Single((await w.Service.ListAsync(new(2026,10,1),new(2026,11,1),default)).Value!);
        Assert.Equal("forbidden",(await w.Service.SaveAsync(made.Id,w.Input() with {Version=1},default)).Error.Code);
        w.Current.UserId=w.Outsider;
        Assert.Empty((await w.Service.ListAsync(new(2026,10,1),new(2026,11,1),default)).Value!);
        Assert.Equal("not_found",(await w.Service.DeleteAsync(made.Id,1,default)).Error.Code);
        w.Current.UserId=w.Creator;w.Current.TenantId=w.OtherTenant;w.Current.Permissions.Add("Tasks.ManageAll");
        Assert.Empty((await w.Service.ListAsync(new(2026,10,1),new(2026,11,1),default)).Value!);
    }

    [Fact]
    public async Task InvalidTimes_ForeignPeople_InaccessibleTasks_AndMissingRowsAreRejected()
    {
        using var w=new World();
        foreach(var bad in new[]{w.Input() with {EndTime="08:00"},w.Input() with {StartTime="25:00"},
            w.Input() with {OrganizerUserId=w.Foreign},w.Input() with {ParticipantIds=[w.Foreign]},
            w.Input() with {TaskIds=[Guid.NewGuid()]},w.Input() with {Agenda=[new("",15,null)]}})
            Assert.True((await w.Service.SaveAsync(null,bad,default)).IsFailure);
        Assert.Empty(w.Db.Meetings);
    }

    [Fact]
    public async Task DeleteChecksVersion_AndRemovesParticipants()
    {
        using var w=new World();var made=(await w.Service.SaveAsync(null,w.Input(),default)).Value!;
        Assert.Equal("conflict",(await w.Service.DeleteAsync(made.Id,9,default)).Error.Code);
        Assert.True((await w.Service.DeleteAsync(made.Id,1,default)).IsSuccess);
        Assert.Empty(w.Db.Meetings);Assert.Empty(w.Db.Set<MeetingParticipant>());
    }

    [Fact]
    public async Task SmsIsOptIn_AndDisabledGatewayDoesNotLoseTheMeeting()
    {
        using var w=new World();var made=await w.Service.SaveAsync(null,w.Input() with {SendInvitationSms=true},default);
        Assert.True(made.IsSuccess);Assert.Contains("غیرفعال",made.Value!.Notice);Assert.Single(w.Db.Meetings);
    }

    [Fact]
    public async Task Scheduling_RejectsPersonOverlapAndRoomOverlap_ButAllowsAdjacentOrDifferentDate()
    {
        using var w=new World();
        var first=w.Input() with{ParticipantIds=[],Location="اتاق ۱۰۱",StartTime="09:00",EndTime="10:00"};
        Assert.True((await w.Service.SaveAsync(null,first,default)).IsSuccess);
        var samePerson=await w.Service.SaveAsync(null,first with{Location="اتاق دیگر",StartTime="09:30",EndTime="10:30"},default);
        Assert.Equal("conflict",samePerson.Error.Code);Assert.Contains("تداخل زمانی",samePerson.Error.Message);
        var sameRoom=await w.Service.SaveAsync(null,first with{OrganizerUserId=w.Person,Location=" اتاق ۱۰۱ ",StartTime="09:15",EndTime="09:45"},default);
        Assert.Equal("conflict",sameRoom.Error.Code);Assert.Contains("مکان",sameRoom.Error.Message);
        Assert.True((await w.Service.SaveAsync(null,first with{StartTime="10:00",EndTime="11:00"},default)).IsSuccess);
        Assert.True((await w.Service.SaveAsync(null,first with{Date=first.Date.AddDays(1)},default)).IsSuccess);
        Assert.Equal(3,await w.Db.Meetings.CountAsync());
    }

    [Fact]
    public async Task Scheduling_EditExcludesItself_ButCannotMoveOntoAnotherMeeting()
    {
        using var w=new World();
        var first=(await w.Service.SaveAsync(null,w.Input() with{ParticipantIds=[],StartTime="08:00",EndTime="09:00"},default)).Value!;
        var second=(await w.Service.SaveAsync(null,w.Input() with{ParticipantIds=[],StartTime="10:00",EndTime="11:00"},default)).Value!;
        Assert.True((await w.Service.SaveAsync(first.Id,first.Details with{Title="اصلاح",Version=first.Version},default)).IsSuccess);
        var collision=await w.Service.SaveAsync(second.Id,second.Details with{StartTime="08:30",EndTime="09:30",Version=second.Version},default);
        Assert.Equal("conflict",collision.Error.Code);
    }

    [Fact]
    public void MeetingPermissionCatalog_ContainsIndependentOperationsAndAllPeopleOverride()
    {
        var names=new CalendarPermissionCatalog().GetPermissions().Select(permission=>permission.Name)
            .Where(name => name.StartsWith("Meetings.", StringComparison.Ordinal)).ToHashSet();
        Assert.Equal(new HashSet<string>{MeetingPermissions.View,MeetingPermissions.Create,MeetingPermissions.Edit,
            MeetingPermissions.InviteHigher,MeetingPermissions.AccessAllPeople},names);
    }

    
[Fact]
public void MeetingPolicies_AreRegisteredWithTheExactPermissionRequirement()
{
    var services = new ServiceCollection();

    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Server=localhost;Database=MeetingTest;Trusted_Connection=True;TrustServerCertificate=True"
            })
        .Build();

    services.AddCalendarMeetings(config);

    using var provider = services.BuildServiceProvider();

    var options = provider
        .GetRequiredService<IOptions<AuthorizationOptions>>()
        .Value;

    foreach (var entry in CalendarPermissions.All)
    {
        var policy = options.GetPolicy(entry.Name);

        Assert.NotNull(policy);

        Assert.Contains(
            policy!.Requirements,
            requirement =>
                requirement is PermissionRequirement permission
                && permission.Permission == entry.Name
        );
    }
}


[Fact]
public async Task Scheduling_RejectsParticipantOverlapEvenWhenOrganizersAndRoomsDiffer()
{
    using var w = new World();

    var first = w.Input() with
    {
        OrganizerUserId = w.Creator,
        ParticipantIds = [w.Person],
        Location = "سالن الف",
        StartTime = "09:00",
        EndTime = "10:00"
    };

    Assert.True(
        (await w.Service.SaveAsync(null, first, default)).IsSuccess
    );

    var conflicting = first with
    {
        OrganizerUserId = w.Person,
        ParticipantIds = [],
        Location = "سالن ب",
        StartTime = "09:30",
        EndTime = "10:30"
    };

    var result = await w.Service.SaveAsync(
        null,
        conflicting,
        default
    );

    Assert.Equal("conflict", result.Error.Code);

    Assert.Contains(
        "تداخل زمانی",
        result.Error.Message
    );

    Assert.Single(w.Db.Meetings);
}



    [Fact]
    public async Task HttpRequiresAuthenticationAndTheMatchingOperationPermission()
    {
        using var w=new World();var b=WebApplication.CreateBuilder();b.WebHost.UseTestServer();
        b.Services.AddSingleton(w.Service);
        b.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,HeaderAuth>("test",_=>{});
        b.Services.AddAuthorization(o=> {foreach(var p in new[]{MeetingPermissions.View,MeetingPermissions.Create,MeetingPermissions.Edit,"Tasks.View"}) o.AddPolicy(p,x=>x.RequireAuthenticatedUser().RequireClaim("permission",p));});
        await using var app=b.Build();app.UseAuthentication();app.UseAuthorization();app.MapMeetingEndpoints();await app.StartAsync();
        var client=app.GetTestClient();const string url="/api/calendar/meetings?from=2026-10-01&to=2026-11-01";
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/calendar/meetings/people-options")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Permissions",MeetingPermissions.Create);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/calendar/meetings/people-options")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/api/calendar/meetings",w.Input())).StatusCode);
    }

    sealed class Current : ICurrentUserContext
    {
        public Guid? UserId{get;set;} public Guid? TenantId{get;set;}public string? Email=>null;public string? IpAddress=>null;
        public HashSet<string> Permissions{get;}=["Tasks.View",MeetingPermissions.View,MeetingPermissions.Create,MeetingPermissions.Edit];
        public bool HasPermission(string p)=>Permissions.Contains(p);
    }
    sealed class World : IDisposable
    {
        public Guid Tenant=Guid.NewGuid(),OtherTenant=Guid.NewGuid(),Creator=Guid.NewGuid(),Person=Guid.NewGuid(),Outsider=Guid.NewGuid(),Foreign=Guid.NewGuid();
        public Current Current{get;}=new();public MeetingDbContext Db{get;}public NexusCoreDbContext Identity{get;}public MeetingService Service{get;}
        public TaskManagementDbContext TasksDb{get;}
        public World()
        {
            Db=new(new DbContextOptionsBuilder<MeetingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Identity=new(new DbContextOptionsBuilder<NexusCoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            foreach(var (id,tenant) in new[]{(Creator,Tenant),(Person,Tenant),(Outsider,Tenant),(Foreign,OtherTenant)})Identity.Users.Add(new User(id,tenant,null,"کاربر","hash"));
            Identity.SaveChanges();Current.UserId=Creator;Current.TenantId=Tenant;
            var taskScope=new SearchAccess(Current);
            TasksDb=new(new DbContextOptionsBuilder<TaskManagementDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,taskScope);
            var taskDirectory = new MeetingTaskDirectory(TasksDb, Current, DispatchProxy.Create<ITaskService,DeniedTasks>(), taskScope);
            Service=new(Db,Identity,Current,DispatchProxy.Create<ISmsTemplateService,DisabledSms>(),NullLogger<MeetingService>.Instance,new TestPeople(this),taskDirectory);
        }
        public MeetingInput Input()=>new("جلسه برنامه‌ریزی","meeting",new(2026,10,30),"09:00","10:00","اتاق جلسات",Creator,"توضیحات",[Person],[],"",[],[]);
        public void Dispose(){Db.Dispose();Identity.Dispose();TasksDb.Dispose();}
    }
    sealed class TestPeople(World w) : IMeetingPeopleScope
    {
        public Task<MeetingPeopleOptions> GetAsync(CancellationToken ct)
        {
            IReadOnlyList<MeetingPerson> people = w.Current.TenantId != w.Tenant ? []
                : w.Current.UserId == w.Outsider ? [new(w.Outsider,"outsider","post","self")]
                : [new(w.Creator,"creator","post","self"),new(w.Person,"person","post","subordinate")];
            return Task.FromResult(new MeetingPeopleOptions(w.Current.HasPermission(MeetingPermissions.Create),false,people));
        }
    }
    sealed class SearchAccess(Current user):ITaskAccessScope
    {
        public bool IsRestricted=>!user.HasPermission("Tasks.ManageAll");public Guid? UserId=>user.UserId;public IReadOnlyList<Guid> GroupIds=>[];
        public Task EnsureLoadedAsync(CancellationToken ct)=>Task.CompletedTask;
        public bool CanManage(TaskItem task)=>!IsRestricted||task.OwnerUserId==UserId;
    }
    public class DeniedTasks : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=> method?.Name=="GetAsync" ? Task.FromResult(Result.Failure<TaskDto>(Error.NotFound("missing"))) : throw new NotSupportedException();
    }
    public class DisabledSms : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name=="IsEnabledAsync" ? Task.FromResult(false) : throw new NotSupportedException();
    }
    public class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder):AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if(!Request.Headers.TryGetValue("X-Test-Permissions",out var p))return Task.FromResult(AuthenticateResult.NoResult());
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("permission",p.ToString())],"test")),"test")));
        }
    }
}
