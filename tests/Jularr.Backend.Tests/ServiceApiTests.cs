using Jularr.Backend.Data.Enums.Service;
using System.Net;
using System.Net.Http.Json;
using System.Security;
using Jularr.Backend.Data.Common;
using Jularr.Backend.Data.Fields.Profile;
using Jularr.Backend.Service.Core;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Jularr.Backend.Tests
{
    [TestClass]
    public sealed class ServiceApiTests
    {
        [TestMethod]
        public async Task Api_ExplicitPublicService_BindsAndUsesSharedErrors()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddJularrServices(
                new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Username=unused;Database=unused;Timeout=1").Build(),
                typeof(ServiceApiTests).Assembly);
            await using var app = builder.Build();
            app.UseExceptionHandler();
            app.UseRateLimiter();
            app.MapJularrServices();
            app.MapGet("/test-token", (HttpContext http, IAntiforgery antiforgery) =>
                antiforgery.GetAndStoreTokens(http).RequestToken);
            await app.StartAsync();
            using var client = app.GetTestClient();

            using var token = await client.GetAsync("/test-token");
            client.DefaultRequestHeaders.Add("Cookie", token.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            client.DefaultRequestHeaders.Add("RequestVerificationToken", await token.Content.ReadAsStringAsync());
            var id = 31L;
            using var success = await client.PostAsJsonAsync("/api/test/" + id, new EchoData("value"));
            Assert.AreEqual(HttpStatusCode.OK, success.StatusCode);
            var result = await success.Content.ReadFromJsonAsync<EchoResult>();
            Assert.AreEqual(id, result!.Id);
            Assert.AreEqual("value", result.Value);
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/not-exposed", new EchoData("value"))).StatusCode);

            using var invalid = await client.PostAsJsonAsync("/api/test/" + id, new EchoData(" "));
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.IsTrue((await invalid.Content.ReadAsStringAsync()).Contains("invalid_data", StringComparison.Ordinal));
            using var failed = await client.PostAsJsonAsync("/api/test/" + id, new EchoData("fail"));
            Assert.AreEqual(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.IsFalse((await failed.Content.ReadAsStringAsync()).Contains("private error", StringComparison.Ordinal));
            using var malformed = await client.PostAsync("/api/test/" + id, new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
            Assert.IsTrue((await malformed.Content.ReadAsStringAsync()).Contains("invalid_request", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task PublicGate_ProfilePermissionAndResource_AreDenied()
        {
            await using var database = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Username=unused;Database=unused").Build();
            var gate = new ServiceGate(new SessionAuthentication(database), new HttpContextAccessor());
            var caller = await gate.RequireCallerAsync(ServiceArea.Public, default);
            await gate.RequireAccessAsync(caller, ServicePermission.Public, default);
            await Assert.ThrowsExactlyAsync<SecurityException>(() => gate.RequireAccessAsync(caller, new("profile.self.read"), default));

            var runtime = new ServiceRuntime(database, gate);
            var service = runtime.Create<ParameterlessService>();
            var response = await service.ExecuteAsync<EchoResult>();
            Assert.AreEqual("no parameters", response.Value);

            // Resource parameters remain mandatory even with the convenient overload.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.Create<EchoService>()
                .ExecuteAsync<EchoResult>(new EchoData("value")));
        }

        public sealed class EchoParameters : BaseParameters
        {
            public EchoParameters(long profileId)
            {
                ProfileId = profileId;
            }

            [DataField(typeof(ProfileFields), nameof(ProfileFields.Id))]
            public long ProfileId { get; }
        }

        public sealed class EchoData : BaseData
        {
            public EchoData(string value)
            {
                Value = value;
            }

            [DataField(typeof(ProfileFields), nameof(ProfileFields.DisplayName))]
            public string Value { get; }
        }

        public sealed class EchoResult : BaseData, IServiceOutput
        {
            public EchoResult(long id, string value)
            {
                Id = id;
                Value = value;
            }

            public long Id { get; }

            public string Value { get; }
        }

        [ServiceEndpoint("POST", "/api/test/{profileId:long}")]
        public sealed class EchoService : PublicService<EchoParameters, EchoData>
        {
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<EchoResult>()];
            protected override ModuleRequirement GetInstanceModules(EchoParameters parameters) => ModuleRequirement.None;
            protected override Task<IServiceOutput> OnExecute() => Data.Value == "fail"
                ? throw new InvalidOperationException("private error")
                : Task.FromResult<IServiceOutput>(new EchoResult(Parameters.ProfileId, Data.Value));
        }

        public sealed class NotExposedService : PublicService<EchoParameters, EchoData>
        {
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<EchoResult>()];
            protected override ModuleRequirement GetInstanceModules(EchoParameters parameters) => ModuleRequirement.None;
        }

        public sealed class EmptyParameters : BaseParameters
        {
        }

        public sealed class ParameterlessService : PublicService<EmptyParameters, NoData>
        {
            protected override ServiceOperationType GetOperationType() => ServiceOperationType.Execute;
            protected override IReadOnlyList<ServiceResultType> GetResultTypes() => [ServiceResultType.Default<EchoResult>()];
            protected override ModuleRequirement GetInstanceModules(EmptyParameters parameters) => ModuleRequirement.None;
            protected override Task<IServiceOutput> OnExecute() =>
                Task.FromResult<IServiceOutput>(new EchoResult(1, "no parameters"));
        }
    }
}
