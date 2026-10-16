// Сборка без вшитого ключа: ключ задаётся в config.ini или командой --setkey <ключ>.
// Для личной сборки рядом генерируется EmbeddedKey.g.cs с ключом внутри (см. build.sh).
namespace Podstrochnik;

internal static class EmbeddedKey
{
    internal const string Value = "";
}
