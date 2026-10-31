using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Organization.Domain;
using Nexus.Organization.Infrastructure;
using Nexus.OrgStructure.Application;
using Nexus.OrgStructure.Domain;
using Nexus.OrgStructure.Infrastructure;
using Nexus.OrgStructure.Permissions;
using Nexus.TaskManagement.Application;
using NexusCore.Application.Messaging;
using NexusCore.Domain.Identity;
using NexusCore.Infrastructure.Persistence;
using NexusCore.SharedKernel.Interfaces;
using Nexus.Calendar.Application.Meetings;
using Nexus.Calendar.Infrastructure.Meetings;
using Nexus.Calendar.Permissions;
using Nexus.Integrations.CalendarOrgStructure;

namespace TejaratBank_API.Tests;

public class MeetingHierarchyTests
{
    [Fact]
    public async Task Managers_ReachOwnBranch_AndParallelPeers_ButNotParallelStaffOrOtherDeputy()
    {
        using var w=new World();
        var choices=await w.Scope.GetAsync(default);
        Assert.True(choices.CanCreate);
        Assert.False(choices.CanInviteHigher);
        Assert.Equal(new HashSet<Guid>{w.ManagerA,w.StaffA,w.ManagerB,w.ChildManager,w.ChildStaff},choices.People.Select(p=>p.Id).ToHashSet());
        w.Current.Permissions.UnionWith([OrgStructurePermissions.AssignTasksToAnyLevel,OrgStructurePermissions.AllCompanies,"Tasks.ManageAll"]);
        Assert.Equal(choices.People.Select(p=>p.Id),(await w.Scope.GetAsync(default)).People.Select(p=>p.Id));
    }

    [Fact]
    public async Task InviteHigher_AddsOnlyActualAncestors_NeverParallelManagersOrOtherDeputiesAndCompanies()
    {
        using var w=new World();w.Current.UserId=w.StaffA;
        var before=await w.Scope.GetAsync(default);
        Assert.Contains(before.People,p=>p.Id==w.StaffB);
        Assert.DoesNotContain(before.People,p=>p.Id==w.ManagerA||p.Id==w.ManagerB);
        w.Current.Permissions.Add(MeetingPermissions.InviteHigher);
        var after=await w.Scope.GetAsync(default);
        Assert.True(after.CanInviteHigher);
        Assert.Contains(after.People,p=>p.Id==w.ManagerA);
        Assert.Contains(after.People,p=>p.Id==w.Deputy);
        Assert.Contains(after.People,p=>p.Id==w.Head);
        Assert.DoesNotContain(after.People,p=>p.Id==w.ManagerB||p.Id==w.OtherDeputyPeer||p.Id==w.OtherCompanyPeer||p.Id==w.Foreign);
    }

    [Fact]
    public async Task AccessAllPeople_BypassesMeetingHierarchyOnly_AndAllowsCreatorWithoutAnActivePost()
    {
        using var w=new World();
        w.Current.Permissions.Add(MeetingPermissions.AccessAllPeople);
        var options=await w.Scope.GetAsync(default);
        Assert.Contains(options.People,p=>p.Id==w.OtherDeputyPeer);
        Assert.Contains(options.People,p=>p.Id==w.OtherCompanyPeer);
        Assert.DoesNotContain(options.People,p=>p.Id==w.Foreign);
        var holding=await w.Org.UserPositions.SingleAsync(h=>h.UserId==w.ManagerA);
        holding.End(DateTimeOffset.UtcNow.AddSeconds(-1));await w.Org.SaveChangesAsync();
        var withoutPost=await w.Scope.GetAsync(default);
        Assert.True(withoutPost.CanCreate);
        Assert.Contains(withoutPost.People,p=>p.Id==w.ManagerA&&p.PositionTitle=="بدون سمت فعال");
        var input=w.Input() with{OrganizerUserId=w.ManagerA,ParticipantIds=[w.OtherCompanyPeer]};
        Assert.True((await w.Service.SaveAsync(null,input,default)).IsSuccess);
    }

    [Fact]
    public async Task ViewingMeetings_FollowsHierarchy_AndAllPeopleGrantSeesTheWholeTenant()
    {
        using var w=new World();
        var peerMeeting=await w.Service.SaveAsync(null,w.Input() with{OrganizerUserId=w.ManagerB,ParticipantIds=[],Title="جلسه هم‌سطح"},default);
        Assert.True(peerMeeting.IsSuccess);
        w.Current.UserId=w.OtherDeputyPeer;
        var remote=await w.Service.SaveAsync(null,w.Input() with{OrganizerUserId=w.OtherDeputyPeer,ParticipantIds=[],Title="جلسه معاونت دیگر",StartTime="11:00",EndTime="12:00"},default);
        Assert.True(remote.IsSuccess);
        w.Current.UserId=w.ManagerA;
        var normal=(await w.Service.ListAsync(new(2026,10,1),new(2026,11,2),default)).Value!;
        Assert.Contains(normal,m=>m.Details.Title=="جلسه هم‌سطح");
        Assert.DoesNotContain(normal,m=>m.Details.Title=="جلسه معاونت دیگر");
        w.Current.Permissions.Add(MeetingPermissions.AccessAllPeople);
        var all=(await w.Service.ListAsync(new(2026,10,1),new(2026,11,2),default)).Value!;
        Assert.Contains(all,m=>m.Details.Title=="جلسه معاونت دیگر");
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("organizer")]
    [InlineData("presenter")]
    [InlineData("action")]
    public async Task ForgedPeople_AreRejectedForEveryRole_WithoutSavingOrSendingSms(string role)
    {
        using var w=new World();w.Current.Permissions.Add(MeetingPermissions.InviteHigher);
        var input=w.Input();
        input=role switch {
            "participant"=>input with{ParticipantIds=[w.StaffB]},
            "organizer"=>input with{OrganizerUserId=w.StaffB},
            "presenter"=>input with{Agenda=[new("بند",15,w.StaffB)]},
            _=>input with{Actions=[new("اقدام",w.StaffB,null)]}
        };
        var saved=await w.Service.SaveAsync(null,input with{SendInvitationSms=true},default);
        Assert.True(saved.IsFailure);Assert.Equal("forbidden",saved.Error.Code);
        Assert.Empty(await w.Meetings.Meetings.ToListAsync());
    }

    [Fact]
    public async Task HigherInvitation_RequiresItsOwnGrant_AndRevocationIsEnforcedOnUpdates()
    {
        using var w=new World();w.Current.UserId=w.StaffA;
        var input=w.Input() with{OrganizerUserId=w.StaffA,ParticipantIds=[w.ManagerA]};
        Assert.Equal("forbidden",(await w.Service.SaveAsync(null,input,default)).Error.Code);
        w.Current.Permissions.Add(MeetingPermissions.InviteHigher);
        var created=await w.Service.SaveAsync(null,input,default);Assert.True(created.IsSuccess);
        w.Current.Permissions.Remove(MeetingPermissions.InviteHigher);
        Assert.Equal("forbidden",(await w.Service.SaveAsync(created.Value!.Id,input with{Version=1},default)).Error.Code);
        Assert.True((await w.Service.SaveAsync(created.Value.Id,input with{ParticipantIds=[],Version=1},default)).IsSuccess);
    }

    [Fact]
    public async Task PermissionAndCurrentAppointment_AreBothRequired_NoBypassWithHigherGrant()
    {
        using var w=new World();w.Current.Permissions.Remove(MeetingPermissions.Create);
        Assert.False((await w.Scope.GetAsync(default)).CanCreate);
        Assert.Equal("forbidden",(await w.Service.SaveAsync(null,w.Input(),default)).Error.Code);
        w.Current.Permissions.Add(MeetingPermissions.Create);w.Current.Permissions.Add(MeetingPermissions.InviteHigher);
        var holding=await w.Org.UserPositions.SingleAsync(h=>h.UserId==w.ManagerA);
        holding.End(DateTimeOffset.UtcNow.AddSeconds(-1));await w.Org.SaveChangesAsync();
        Assert.False((await w.Scope.GetAsync(default)).CanCreate);
        Assert.Empty((await w.Scope.GetAsync(default)).People);
        Assert.Equal("forbidden",(await w.Service.SaveAsync(null,w.Input(),default)).Error.Code);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("account")]
    [InlineData("position")]
    [InlineData("unit")]
    public async Task InactiveOrFuturePeople_NeverAppearInOptions(string reason)
    {
        using var w=new World();
        var holding=await w.Org.UserPositions.SingleAsync(h=>h.UserId==w.StaffA);
        if(reason=="expired")holding.End(DateTimeOffset.UtcNow.AddDays(-1));
        if(reason=="future"){
            w.Org.UserPositions.Remove(holding);
            w.Org.UserPositions.Add(new UserPosition(Guid.NewGuid(),w.Tenant,w.StaffA,holding.UnitPositionId,DateTimeOffset.UtcNow.AddDays(1)));
        }
        if(reason=="account")(await w.Identity.Users.SingleAsync(u=>u.Id==w.StaffA)).SetActive(false);
        if(reason=="position"){
            var seat=await w.Org.UnitPositions.SingleAsync(s=>s.Id==holding.UnitPositionId);
            var position=await w.Org.Positions.SingleAsync(p=>p.Id==seat.PositionId);position.Update(position.Title,position.ParentPositionId,false);
        }
        if(reason=="unit"){
            var unit=await w.Units.OrganizationUnits.SingleAsync(u=>u.Id==w.UnitA);unit.Update(unit.Name,unit.ManagerUserId,false);
        }
        await w.SaveAsync();
        Assert.DoesNotContain((await w.Scope.GetAsync(default)).People,p=>p.Id==w.StaffA);
    }

    [Fact]
    public async Task TaskAdministrator_CannotReadUnrelatedMeetings_AndCompanyTransferInvalidatesNewInvites()
    {
        using var w=new World();var made=await w.Service.SaveAsync(null,w.Input(),default);Assert.True(made.IsSuccess);
        w.Current.UserId=w.OtherDeputyPeer;w.Current.Permissions.Add("Tasks.ManageAll");
        Assert.Empty((await w.Service.ListAsync(new(2026,10,1),new(2026,11,1),default)).Value!);
        w.Current.UserId=w.ManagerA;
        var placement=await w.Org.UserAssignments.SingleAsync(a=>a.UserId==w.StaffA);
        placement.Set(w.Company2,w.UnitA,placement.PositionId,placement.ManagerUserId);await w.Org.SaveChangesAsync();
        Assert.Equal("forbidden",(await w.Service.SaveAsync(made.Value!.Id,w.Input() with{Version=1},default)).Error.Code);
    }

    sealed class Current : ICurrentUserContext
    {
        public Guid? UserId{get;set;}public Guid? TenantId{get;set;}public string? Email=>null;public string? IpAddress=>null;
        public HashSet<string> Permissions{get;}=["Tasks.View",MeetingPermissions.View,MeetingPermissions.Create,MeetingPermissions.Edit];
        public bool HasPermission(string p)=>Permissions.Contains(p);
    }
    sealed class World : IDisposable
    {
        public Guid Tenant=Guid.NewGuid(),OtherTenant=Guid.NewGuid(),Company=Guid.NewGuid(),Company2=Guid.NewGuid();
        public Guid Head=Guid.NewGuid(),Deputy=Guid.NewGuid(),ManagerA=Guid.NewGuid(),ManagerB=Guid.NewGuid(),StaffA=Guid.NewGuid(),StaffB=Guid.NewGuid(),ChildManager=Guid.NewGuid(),ChildStaff=Guid.NewGuid(),OtherDeputyPeer=Guid.NewGuid(),OtherCompanyPeer=Guid.NewGuid(),Foreign=Guid.NewGuid();
        public Guid UnitA=Guid.NewGuid();
        public Current Current{get;}=new();
        public OrgStructureDbContext Org{get;}=new(new DbContextOptionsBuilder<OrgStructureDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public OrganizationDbContext Units{get;}=new(new DbContextOptionsBuilder<OrganizationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public NexusCoreDbContext Identity{get;}=new(new DbContextOptionsBuilder<NexusCoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public MeetingDbContext Meetings{get;}=new(new DbContextOptionsBuilder<MeetingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public HierarchyMeetingPeopleScope Scope{get;}public MeetingService Service{get;}
        public World()
        {
            Current.UserId=ManagerA;Current.TenantId=Tenant;
            Org.Companies.AddRange(new Company(Company,Tenant,"سازمان یک","1",null,Head),new Company(Company2,Tenant,"سازمان دو","2",null,null));
            var boundary=new NodeType(Guid.NewGuid(),Tenant,"deputy","معاونت",true);
            var department=new NodeType(Guid.NewGuid(),Tenant,"department","اداره",false);Org.NodeTypes.AddRange(boundary,department);
            var deputyUnit=Node("معاونت خودم",Deputy,null,boundary.Id);
            var otherDeputy=Node("معاونت دیگر",null,null,boundary.Id);
            Node("اداره من",ManagerA,deputyUnit,department.Id,UnitA);
            var unitB=Node("اداره موازی",ManagerB,deputyUnit,department.Id);
            var child=Node("اداره زیرمجموعه",ChildManager,UnitA,department.Id);
            Org.CompanyUnits.AddRange(new CompanyUnit(Guid.NewGuid(),Tenant,Company,deputyUnit),new CompanyUnit(Guid.NewGuid(),Tenant,Company,otherDeputy));
            var root=new Position(Guid.NewGuid(),Tenant,"رئیس");var deputy=new Position(Guid.NewGuid(),Tenant,"معاون",root.Id);
            var manager=new Position(Guid.NewGuid(),Tenant,"مدیر",deputy.Id);var staff=new Position(Guid.NewGuid(),Tenant,"کارشناس",manager.Id);
            Org.Positions.AddRange(root,deputy,manager,staff);
            Person(Head,null,root,0);Person(Deputy,deputyUnit,deputy,0);
            Person(ManagerA,UnitA,manager,0);Person(StaffA,UnitA,staff,1);
            Person(ManagerB,unitB,manager,0);Person(StaffB,unitB,staff,1);
            Person(ChildManager,child,manager,0);Person(ChildStaff,child,staff,1);
            Person(OtherDeputyPeer,otherDeputy,manager,0);Person(OtherCompanyPeer,null,manager,0,Company2);
            Identity.Users.Add(new User(Foreign,OtherTenant,null,"فرد سازمان دیگر","hash"));
            SaveAsync().GetAwaiter().GetResult();
            var repository=new OrgStructureRepository(Org);var units=new OrganizationUnitRepository(Units);
            Scope=new(repository,new OrgGraphProvider(units,repository),units,Identity,Current);
            Service=new(Meetings,Identity,Current,DispatchProxy.Create<ISmsTemplateService,MeetingTests.DisabledSms>(),NullLogger<MeetingService>.Instance,Scope,new NoTasks());
        }
        sealed class NoTasks : IMeetingTaskDirectory
        {
            public Task<NexusCore.SharedKernel.Results.Result<MeetingTaskPage>> SearchAsync(string? search, int pageNumber, int pageSize, string? ids, CancellationToken cancellationToken) =>
                Task.FromResult(NexusCore.SharedKernel.Results.Result.Failure<MeetingTaskPage>(NexusCore.SharedKernel.Results.Error.NotFound("وظیفه‌ای در دسترس نیست.")));
            public Task<bool> CanAccessAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(false);
        }
        Guid Node(string name,Guid? manager,Guid? parent,Guid type,Guid? id=null)
        {
            var unit=new OrganizationUnit(id??Guid.NewGuid(),Tenant,name,manager);unit.SetParent(parent);unit.SetNodeType(type);Units.OrganizationUnits.Add(unit);return unit.Id;
        }
        void Person(Guid person,Guid? unit,Position position,int order,Guid? company=null)
        {
            var c=company??Company;
            Identity.Users.Add(new User(person,Tenant,null,person.ToString(),"hash"));
            var assignment=new UserAssignment(Guid.NewGuid(),Tenant,person);assignment.Set(c,unit,position.Id,null);Org.UserAssignments.Add(assignment);
            var seat=new UnitPosition(Guid.NewGuid(),Tenant,c,unit,position.Id,order,Guid.NewGuid().ToString());Org.UnitPositions.Add(seat);
            Org.UserPositions.Add(new UserPosition(Guid.NewGuid(),Tenant,person,seat.Id,DateTimeOffset.UtcNow.AddDays(-1)));
        }
        public MeetingInput Input()=>new("جلسه","meeting",new(2026,10,31),"09:00","10:00","",ManagerA,"",[StaffA],[],"",[],[]);
        public async Task SaveAsync(){await Identity.SaveChangesAsync();await Org.SaveChangesAsync();await Units.SaveChangesAsync();}
        public void Dispose(){Identity.Dispose();Org.Dispose();Units.Dispose();Meetings.Dispose();}
    }
}
