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

    public static Exception AttributeNotFound(CkId<CkAttributeId> modelAttributeId, CkId<CkTypeId> ckTypeId)
    {
        return new CommandExecutionFailedException($"Attribute '{modelAttributeId}' does not exist in type '{ckTypeId}'.");
    }

    public static Exception RecordNotFound(CkId<CkRecordId> ckRecordId, CkId<CkTypeId> ckTypeId)
    {
        return new CommandExecutionFailedException($"Record '{ckRecordId}' does not exist at type '{ckTypeId}'.");
    }

    public static Exception CkModelsMissing(string tenantId, ICollection<CkModelId> ckModelIds)
    {
        return new CommandExecutionFailedException($"Models '{string.Join(", ", ckModelIds)}' are missing in tenant '{tenantId}'.");
    }
}