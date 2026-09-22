using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace BotBehavior.BuildCheck;

// 仅编译服务端依赖注入与生命周期接口，不部署到服务端。
[Injectable]
public sealed class ServerCompileProbe(ISptLogger<ServerCompileProbe> logger) : IOnLoad
{
    public static Type[] ReferencedTypes => [typeof(BotController), typeof(IModMetadata)];

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        logger.Info("Build check");
        return Task.CompletedTask;
    }
}
