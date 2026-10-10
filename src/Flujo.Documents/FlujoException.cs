namespace Flujo.Documents;

public class FlujoException : Exception
{
    public FlujoException(string message) : base(message)
    {
    }

    public FlujoException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class PathNotAllowedException : FlujoException
{
    public PathNotAllowedException(string message) : base(message)
    {
    }
}

public sealed class DocumentValidationException : FlujoException
{
    public DocumentValidationException(string message) : base(message)
    {
    }

    public DocumentValidationException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class TemplateException : FlujoException
{
    public TemplateException(string message) : base(message)
    {
    }
}

public sealed class ExternalServiceException : FlujoException
{
    public ExternalServiceException(string message) : base(message)
    {
    }

    public ExternalServiceException(string message, Exception inner) : base(message, inner)
    {
    }
}
