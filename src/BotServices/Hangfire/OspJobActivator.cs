using System;
using Hangfire;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices.Hangfire;

public class OctoJobActivator : JobActivator
{
    private readonly IServiceProvider _serviceProvider;

    public OctoJobActivator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public override object ActivateJob(Type type)
    {
        return _serviceProvider.GetService(type);
    }
}
