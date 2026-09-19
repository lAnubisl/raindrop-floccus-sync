using RaindropToFloccus.Models;

namespace RaindropToFloccus.Interfaces;

public interface IXbelDocumentSerializer
{
    void Validate(XbelDocument document);

    XbelDocument Parse(string content);

    string Serialize(XbelDocument document);
}
