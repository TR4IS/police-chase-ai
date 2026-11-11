using AssettoServer.Server.Plugin;
using Autofac;

namespace CopBotPlugin;

public class CopBotModule : AssettoServerModule
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CopBot>().AsSelf().SingleInstance().AutoActivate();
    }
}
