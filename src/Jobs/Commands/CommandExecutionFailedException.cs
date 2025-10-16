using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.Jobs.Commands;

internal class CommandExecutionFailedException : Exception
{
    public CommandExecutionFailedException()
    {
    }

    public CommandExecutionFailedException(string message) : base(message)
    {
    }

    public CommandExecutionFailedException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception CannotDeserializeModel(string filePath)
    {
        return new CommandExecutionFailedException($"Cannot deserialize model from file '{filePath}'.");
    }

    public static Exception ValidationErrors()
    {
        return new CommandExecutionFailedException("Validation errors occurred while loading model.");
    }

    public static Exception BulkImportError()
    {
        return new CommandExecutionFailedException("Write operation was not acknowledged by database.");
    }

    public static Exception BulkImportError(Exception e)
    {
        return new CommandExecutionFailedException("Write operation was not acknowledged by database.", e);
    }

    public static Exception CannotDeserializeModelFromString(string jsonText)
    {
        return new CommandExecutionFailedException($"Cannot deserialize model from string '{jsonText}'.");
    }

    public static Exception QueryNotFound(OctoObjectId queryId)
    {
        return new CommandExecutionFailedException($"Query '{queryId}‘ does not exist.");
    }

    public static Exception QueryCkTypeIdNotSet(OctoObjectId queryId)
    {
        return new CommandExecutionFailedException($"Query '{queryId}‘ has no QueryCkTypeId attribute set.");
    }

    public static Exception AttributeNotFound<TKey>(CkId<CkAttributeId> modelAttributeId, string elementType, CkId<TKey> ckId)
        where TKey : IComparable<TKey>, ICkElementId
    {
        return new CommandExecutionFailedException($"Attribute '{modelAttributeId}' does not exist at {elementType} '{ckId}'.");
    }

    public static Exception RecordNotFound<TKey>(CkId<CkRecordId> ckRecordId, string elementType,  CkId<TKey> ckId)
        where TKey : IComparable<TKey>, ICkElementId
    {
        return new CommandExecutionFailedException($"Record '{ckRecordId}' does not exist at {elementType} '{ckId}'.");
    }

    public static Exception CkModelsMissing(string tenantId, ICollection<CkModelId> ckModelIds)
    {
        return new CommandExecutionFailedException($"Models '{string.Join(", ", ckModelIds)}' are missing in tenant '{tenantId}'.");
    }
}