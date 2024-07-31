namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal abstract class CommandBase
{
    protected static bool CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }
    
    protected static void CheckAndThrowCancellation(CancellationToken? cancellationToken)
    {
        if (CheckCancellation(cancellationToken))
        {
            throw new OperationCanceledException();
        }
    }
    
}