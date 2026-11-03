namespace Minecraft;

public sealed class PackSessionExpiredException() : InvalidOperationException(
    "Файлы выбранной версии сборки больше недоступны. Повторите «Играть», чтобы проверить новую версию.");
