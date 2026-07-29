package minecraft.portable.identity;

public final class PortableXaeroWaypointHooks {
    private static final String EXECUTE = "execute";
    private static final String IN = "in";
    private static final String RUN = "run";
    private static final String WAYPOINT_COMMAND = "portable_waypoint_tp";
    private static final String DIMENSION_COMMAND = "portable_waypoint_tp_dimension";

    private PortableXaeroWaypointHooks() {
    }

    public static String rewriteCrossDimensionCommand(String command) {
        if (command == null || command.isEmpty()) {
            return command;
        }

        String candidate = command.charAt(0) == '/' ? command.substring(1) : command;
        String[] tokens = candidate.split(" ", -1);
        if ((tokens.length != 8 && tokens.length != 9) ||
            !EXECUTE.equals(tokens[0]) ||
            !IN.equals(tokens[1]) ||
            !isResourceLocation(tokens[2]) ||
            !RUN.equals(tokens[3]) ||
            !WAYPOINT_COMMAND.equals(tokens[4]) ||
            !isCanonicalInteger(tokens[5]) ||
            !isWaypointY(tokens[6]) ||
            !isCanonicalInteger(tokens[7]) ||
            (tokens.length == 9 && !isCanonicalInteger(tokens[8]))) {
            return command;
        }

        StringBuilder rewritten = new StringBuilder(candidate.length() + 16)
            .append(DIMENSION_COMMAND)
            .append(' ')
            .append(tokens[2]);
        for (int index = 5; index < tokens.length; index++) {
            rewritten.append(' ').append(tokens[index]);
        }
        return rewritten.toString();
    }

    private static boolean isResourceLocation(String value) {
        if (value.length() < 3 || value.length() > 320) {
            return false;
        }
        int separator = value.indexOf(':');
        if (separator <= 0 ||
            separator == value.length() - 1 ||
            separator != value.lastIndexOf(':')) {
            return false;
        }
        for (int index = 0; index < separator; index++) {
            char character = value.charAt(index);
            if (!isLowercaseLetterOrDigit(character) &&
                character != '_' &&
                character != '-' &&
                character != '.') {
                return false;
            }
        }
        for (int index = separator + 1; index < value.length(); index++) {
            char character = value.charAt(index);
            if (!isLowercaseLetterOrDigit(character) &&
                character != '_' &&
                character != '-' &&
                character != '.' &&
                character != '/') {
                return false;
            }
        }
        return true;
    }

    private static boolean isLowercaseLetterOrDigit(char character) {
        return (character >= 'a' && character <= 'z') ||
            (character >= '0' && character <= '9');
    }

    private static boolean isWaypointY(String value) {
        if (isCanonicalInteger(value)) {
            return true;
        }
        if (!value.endsWith(".5")) {
            return false;
        }
        String whole = value.substring(0, value.length() - 2);
        return "-0".equals(whole) || isCanonicalInteger(whole);
    }

    private static boolean isCanonicalInteger(String value) {
        if (value.isEmpty() || value.length() > 11) {
            return false;
        }
        try {
            int parsed = Integer.parseInt(value);
            return Integer.toString(parsed).equals(value);
        } catch (NumberFormatException exception) {
            return false;
        }
    }
}
