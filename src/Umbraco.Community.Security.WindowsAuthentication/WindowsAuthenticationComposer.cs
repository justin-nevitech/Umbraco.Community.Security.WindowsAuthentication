using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace Umbraco.Community.Security.WindowsAuthentication;

public sealed class WindowsAuthenticationComposer : IComposer
{
    // PrePipeline is the first thing app.UseUmbraco().WithMiddleware() runs, ahead of UseRouting/UseAuthentication.
    public void Compose(IUmbracoBuilder builder)
        => builder.Services.Configure<UmbracoPipelineOptions>(options =>
            options.AddFilter(new UmbracoPipelineFilter(WindowsAuthenticationDefaults.PipelineFilterName)
            {
                PrePipeline = app => app.UseMiddleware<WindowsAuthenticationMiddleware>(),
            }));
}
