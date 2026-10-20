namespace Minecraft;

public sealed class PackSessionExpiredException() : InvalidOperationException(
    "Файлы выбранной версии сборки больше недоступны. Перезапустите лаунчер, чтобы проверить новую версию.");
