import org.lwjgl.glfw.GLFW;
import org.lwjgl.glfw.GLFWNativeWin32;

public final class GlfwInitialWindow {
    public static void main(String[] args) throws Exception {
        if (!GLFW.glfwInit()) throw new AssertionError("GLFW initialization failed");
        try {
            GLFW.glfwDefaultWindowHints();
            GLFW.glfwWindowHint(GLFW.GLFW_CLIENT_API, GLFW.GLFW_NO_API);
            long window = GLFW.glfwCreateWindow(320, 200, "Minecraft 26.3", 0, 0);
            if (window == 0) throw new AssertionError("Window creation failed");
            try {
                WindowReceipt.check(GLFWNativeWin32.glfwGetWin32Window(window));
                GLFW.glfwSetWindowTitle(window, "Minecraft loader progress");
                GLFW.glfwSetWindowIcon(window, null);
                WindowReceipt.check(GLFWNativeWin32.glfwGetWin32Window(window));
                GLFW.glfwWindowHint(GLFW.GLFW_FOCUSED, GLFW.GLFW_FALSE);
                long unfocused = GLFW.glfwCreateWindow(320, 200, "Unfocused window", 0, 0);
                if (unfocused == 0) throw new AssertionError("Unfocused creation failed");
                try {
                    if (GLFW.glfwGetWindowAttrib(unfocused, GLFW.GLFW_FOCUSED) != GLFW.GLFW_FALSE)
                        throw new AssertionError("Unfocused creation stole focus");
                    if (GLFW.glfwGetWindowAttrib(unfocused, GLFW.GLFW_FOCUS_ON_SHOW) != GLFW.GLFW_TRUE)
                        throw new AssertionError("Later focus-on-show policy changed");
                } finally { GLFW.glfwDestroyWindow(unfocused); }
                GLFW.glfwWindowHint(GLFW.GLFW_VISIBLE, GLFW.GLFW_FALSE);
                long hidden = GLFW.glfwCreateWindow(320, 200, "Hidden native window", 0, 0);
                if (hidden == 0) throw new AssertionError("Hidden window creation failed");
                try {
                    if (GLFW.glfwGetWindowAttrib(hidden, GLFW.GLFW_VISIBLE) != GLFW.GLFW_FALSE)
                        throw new AssertionError("An intentionally hidden window was exposed");
                } finally { GLFW.glfwDestroyWindow(hidden); }
                GLFW.glfwPollEvents();
            } finally { GLFW.glfwDestroyWindow(window); }
        } finally { GLFW.glfwTerminate(); }
    }
}
