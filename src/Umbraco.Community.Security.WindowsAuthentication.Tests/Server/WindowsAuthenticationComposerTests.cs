using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Server;

[TestFixture]
public class WindowsAuthenticationComposerTests
{
    [Test]
    public void Adds_a_single_pre_pipeline_filter_and_nothing_else()
    {
        IServiceCollection services = Compose();

        UmbracoPipelineOptions options = services.BuildServiceProvider().GetRequiredService<IOptions<UmbracoPipelineOptions>>().Value;

        IUmbracoPipelineFilter filter = options.PipelineFilters.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(filter.Name, Is.EqualTo(WindowsAuthenticationDefaults.PipelineFilterName));
            Assert.That(filter, Is.TypeOf<UmbracoPipelineFilter>());
            var pipelineFilter = (UmbracoPipelineFilter)filter;
            Assert.That(pipelineFilter.PrePipeline, Is.Not.Null);
            Assert.That(pipelineFilter.PreRouting, Is.Null);
            Assert.That(pipelineFilter.PostRouting, Is.Null);
            Assert.That(pipelineFilter.PostPipeline, Is.Null);
            Assert.That(pipelineFilter.Endpoints, Is.Null);
        }
    }

    [Test]
    public void Registers_no_services_other_than_the_pipeline_options()
    {
        IServiceCollection services = Compose();

        // services.Configure also adds the open generic options infrastructure, which is shared and idempotent.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                services.Select(d => d.ServiceType).Where(t => t.IsGenericTypeDefinition is false),
                Is.EqualTo(new[] { typeof(IConfigureOptions<UmbracoPipelineOptions>) }));
            Assert.That(
                services.Select(d => d.ServiceType).Where(t => t.IsGenericTypeDefinition).Select(t => t.Namespace),
                Is.All.EqualTo("Microsoft.Extensions.Options"));
        }
    }

    [Test]
    public async Task Pre_pipeline_filter_restores_the_header_ahead_of_the_rest_of_the_pipeline()
    {
        IServiceCollection services = Compose().AddLogging();
        ServiceProvider provider = services.BuildServiceProvider();
        IUmbracoPipelineFilter filter = provider.GetRequiredService<IOptions<UmbracoPipelineOptions>>().Value.PipelineFilters.Single();

        var app = new ApplicationBuilder(provider);
        filter.OnPrePipeline(app);
        string? seenAuthorization = null;
        app.Run(context =>
        {
            seenAuthorization = context.Request.Headers.Authorization;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Headers[WindowsAuthenticationDefaults.HeaderName] = "Bearer abc";
        await app.Build()(httpContext);

        Assert.That(seenAuthorization, Is.EqualTo("Bearer abc"));
    }

    private static IServiceCollection Compose()
    {
        var services = new ServiceCollection();
        IUmbracoBuilder builder = Substitute.For<IUmbracoBuilder>();
        builder.Services.Returns(services);

        new WindowsAuthenticationComposer().Compose(builder);

        return services;
    }
}
