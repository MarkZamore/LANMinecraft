package minecraft.portable.identity;

import java.nio.file.*;
import java.util.*;
import java.util.zip.*;
import org.objectweb.asm.*;
import org.objectweb.asm.tree.*;

/** Executes the real entry transformer. The local hook substitute isolates bytecode/UI scope. */
public final class SteamLanEntryProbe {
    public static Component seenHeader, seenHidden;
    public static String seenMotd, seenAddress;
    public static final class Component {
        public final String text;
        public Component(String text) { this.text=text; }
    }
    public static final class LanServer {
        public final String world, motd, address;
        public final boolean registered;
        public LanServer(String world, String motd, boolean registered) {
            this.world=world;this.motd=motd;this.registered=registered;
            this.address=registered?"portable-steam-201.invalid":"192.168.0.110:49441";
        }
        public String getMotd() { return motd; }
        public String getAddress() { return address; }
    }
    public static void recordHeader(Component value) { seenHeader=value; }
    public static void recordHidden(Component value) { seenHidden=value; }
    public static void recordMotd(String value) { seenMotd=value; }
    public static void recordAddress(String value) { seenAddress=value; }
    public static Object substituteHeader(Object original,Object value) {
        var server=(LanServer)value;
        return server.registered?new Component(server.world):original;
    }
    public static String substituteAddress(String original,Object value) { return ((LanServer)value).registered?"Steam":original; }
    private static final String SELF="minecraft/portable/identity/SteamLanEntryProbe";
    private static final String COMPONENT="L"+SELF+"$Component;", SERVER="L"+SELF+"$LanServer;";
    private static final String PREFIX="minecraft.portable.identity.";
    private static void check(boolean value,String reason) { if(!value)throw new AssertionError(reason); }
    private static void configure(String target,String header,String server,String render,String narration) {
        Map.of("steamDiscoveryEnabled","true","steamDiscoveryLanEntryClasses",target,
            "steamDiscoveryLanEntryHeaderFields",header,"steamDiscoveryLanEntryServerFields",server,
            "steamDiscoveryLanEntryRenderMethods",render,"steamDiscoveryLanEntryNarrationMethods",narration,
            "steamDiscoveryLanServerClasses",LanServer.class.getName(),"steamDiscoveryLanAddressMethods","getAddress")
            .forEach((key,value)->System.setProperty(PREFIX+key,value));
    }
    public static void main(String[] args) throws Exception {
        if(args.length!=0) { actual(args);return; }
        var transformer=new PortableSteamLanEntryTransformer();
        for(String draw:new String[]{"m_6311_","render","extractContent"}) {
            String target="fixture/LanEntry"+draw;
            configure(target,"header","server",draw,"narration");
            byte[] original=fixture(target,draw,1,false), changed=transformer.transform(null,null,target,null,null,original);
            check(changed!=null,"Native entry was not changed");
            check(transformer.transform(null,null,target,null,null,changed)==null,"Repeated transform double-wrapped a header");
            Class<?> type=new ClassLoader(SteamLanEntryProbe.class.getClassLoader()) {
                Class<?> define() {
                    byte[] substitute=hookSubstitute();
                    defineClass("minecraft.portable.identity.PortableSteamDiscoveryHooks",substitute,0,substitute.length);
                    return defineClass(target.replace('/','.'),changed,0,changed.length);
                }
            }.define();
            for(boolean registered:new boolean[]{true,false}) {
                LanServer server=new LanServer("Новый мир","Mitochka",registered);
                Object entry=type.getConstructor(LanServer.class).newInstance(server);
                type.getMethod(draw).invoke(entry);
                check(seenHeader.text.equals(registered?"Новый мир":"LAN World"),"Wrong native or registered Steam header");
                check(seenHidden.text.equals("Hidden address"),"Unrelated native address label changed");
                check(seenMotd.equals("Mitochka"),"Host MOTD line or native layout changed");
                check(seenAddress.equals(registered?"Steam":server.address),"Synthetic Steam address leaked or native address changed");
                check(server.getAddress().equals(server.address),"Native address getter or connection target changed");
                check(type.getMethod("joinAddress").invoke(entry).equals(server.address),"Unmapped join address call was changed");
                Component narration=(Component)type.getMethod("narration").invoke(entry);
                check(narration.text.equals(registered?"Новый мир":"LAN World"),"Narration is inconsistent with the header");
                Component unrelated=(Component)type.getMethod("unrelatedHeaderRead").invoke(entry);
                check(unrelated.text.equals("LAN World"),"Unmapped native method was modified");
            }
            System.setProperty(PREFIX+"steamDiscoveryEnabled","false");
            check(transformer.transform(null,null,target,null,null,original)==null,"Disabled discovery changed a row");
            System.setProperty(PREFIX+"steamDiscoveryEnabled","true");
            check(transformer.transform(null,null,target+"Other",null,null,original)==null,"Unmapped native or saved-server class changed");
            System.setProperty(PREFIX+"steamDiscoveryLanAddressMethods","unmappedAddress");
            try { transformer.transform(null,null,target,null,null,original);throw new AssertionError("Missing native address getter accepted"); }
            catch(IllegalStateException expected) { }
            System.setProperty(PREFIX+"steamDiscoveryLanAddressMethods","getAddress");
            ClassNode ambiguousAddress=new ClassNode();new ClassReader(original).accept(ambiguousAddress,0);
            for(MethodNode method:ambiguousAddress.methods) if(method.name.equals(draw))
                for(AbstractInsnNode instruction:method.instructions.toArray()) if(instruction instanceof MethodInsnNode call && call.name.equals("getAddress")) {
                    InsnList duplicate=new InsnList();duplicate.add(new VarInsnNode(Opcodes.ALOAD,0));
                    duplicate.add(new FieldInsnNode(Opcodes.GETFIELD,target,"server",SERVER));
                    duplicate.add(new MethodInsnNode(Opcodes.INVOKEVIRTUAL,SELF+"$LanServer","getAddress","()Ljava/lang/String;",false));
                    duplicate.add(new InsnNode(Opcodes.POP));method.instructions.insert(call,duplicate);break;
                }
            ClassWriter ambiguousWriter=new ClassWriter(ClassWriter.COMPUTE_MAXS);ambiguousAddress.accept(ambiguousWriter);
            try { transformer.transform(null,null,target,null,null,ambiguousWriter.toByteArray());throw new AssertionError("Ambiguous display-address sites accepted"); }
            catch(IllegalStateException expected) { }
            for(int reads:new int[]{0,2}) {
                byte[] unsupported=fixture(target,draw,reads,false);
                try { transformer.transform(null,null,target,null,null,unsupported);throw new AssertionError("Ambiguous header use accepted"); }
                catch(IllegalStateException expected) { }
            }
            try { transformer.transform(null,null,target,null,null,fixture(target,draw,1,true));throw new AssertionError("Static server field accepted"); }
            catch(IllegalStateException expected) { }
            ClassNode partial=new ClassNode();new ClassReader(changed).accept(partial,0);
            for(MethodNode method:partial.methods) if(method.name.equals("narration"))
                for(AbstractInsnNode instruction:method.instructions.toArray()) if(instruction instanceof MethodInsnNode call && call.name.equals("worldTitle")) {
                    method.instructions.remove(call);break;
                }
            ClassWriter writer=new ClassWriter(0);partial.accept(writer);
            try { transformer.transform(null,null,target,null,null,writer.toByteArray());throw new AssertionError("Partially patched row accepted"); }
            catch(IllegalStateException expected) { }
        }
        System.out.println("PASS: native Steam headers and narration use the world name; displayed transport is Steam while native/join addresses, host line, hidden labels and saved entries are preserved. Three draw shapes and strict guards verified; no game or Steam started.");
    }
    private static byte[] fixture(String name,String draw,int reads,boolean staticServer) {
        ClassWriter w=new ClassWriter(ClassWriter.COMPUTE_MAXS);
        w.visit(Opcodes.V17,Opcodes.ACC_PUBLIC,name,null,"java/lang/Object",null);
        w.visitField(Opcodes.ACC_PRIVATE|Opcodes.ACC_STATIC|Opcodes.ACC_FINAL,"header",COMPONENT,null,null).visitEnd();
        w.visitField(Opcodes.ACC_PRIVATE|Opcodes.ACC_STATIC|Opcodes.ACC_FINAL,"hidden",COMPONENT,null,null).visitEnd();
        w.visitField(Opcodes.ACC_PRIVATE|(staticServer?Opcodes.ACC_STATIC:0),"server",SERVER,null,null).visitEnd();
        MethodVisitor m=w.visitMethod(Opcodes.ACC_STATIC,"<clinit>","()V",null,null);m.visitCode();
        for(String[] entry:new String[][]{{"header","LAN World"},{"hidden","Hidden address"}}) {
            m.visitTypeInsn(Opcodes.NEW,SELF+"$Component");m.visitInsn(Opcodes.DUP);m.visitLdcInsn(entry[1]);
            m.visitMethodInsn(Opcodes.INVOKESPECIAL,SELF+"$Component","<init>","(Ljava/lang/String;)V",false);
            m.visitFieldInsn(Opcodes.PUTSTATIC,name,entry[0],COMPONENT);
        }
        m.visitInsn(Opcodes.RETURN);m.visitMaxs(0,0);m.visitEnd();
        m=w.visitMethod(Opcodes.ACC_PUBLIC,"<init>","("+SERVER+")V",null,null);m.visitCode();
        m.visitVarInsn(Opcodes.ALOAD,0);m.visitMethodInsn(Opcodes.INVOKESPECIAL,"java/lang/Object","<init>","()V",false);
        if(!staticServer)m.visitVarInsn(Opcodes.ALOAD,0);m.visitVarInsn(Opcodes.ALOAD,1);
        m.visitFieldInsn(staticServer?Opcodes.PUTSTATIC:Opcodes.PUTFIELD,name,"server",SERVER);
        m.visitInsn(Opcodes.RETURN);m.visitMaxs(0,0);m.visitEnd();
        m=w.visitMethod(Opcodes.ACC_PUBLIC,draw,"()V",null,null);m.visitCode();
        for(int i=0;i<reads;i++) {m.visitFieldInsn(Opcodes.GETSTATIC,name,"header",COMPONENT);m.visitMethodInsn(Opcodes.INVOKESTATIC,SELF,"recordHeader","("+COMPONENT+")V",false);}
        m.visitFieldInsn(Opcodes.GETSTATIC,name,"hidden",COMPONENT);m.visitMethodInsn(Opcodes.INVOKESTATIC,SELF,"recordHidden","("+COMPONENT+")V",false);
        if(!staticServer)m.visitVarInsn(Opcodes.ALOAD,0);m.visitFieldInsn(staticServer?Opcodes.GETSTATIC:Opcodes.GETFIELD,name,"server",SERVER);
        m.visitMethodInsn(Opcodes.INVOKEVIRTUAL,SELF+"$LanServer","getMotd","()Ljava/lang/String;",false);
        m.visitMethodInsn(Opcodes.INVOKESTATIC,SELF,"recordMotd","(Ljava/lang/String;)V",false);
        if(!staticServer)m.visitVarInsn(Opcodes.ALOAD,0);m.visitFieldInsn(staticServer?Opcodes.GETSTATIC:Opcodes.GETFIELD,name,"server",SERVER);
        m.visitMethodInsn(Opcodes.INVOKEVIRTUAL,SELF+"$LanServer","getAddress","()Ljava/lang/String;",false);
        m.visitMethodInsn(Opcodes.INVOKESTATIC,SELF,"recordAddress","(Ljava/lang/String;)V",false);
        m.visitInsn(Opcodes.RETURN);m.visitMaxs(0,0);m.visitEnd();
        for(String method:new String[]{"narration","unrelatedHeaderRead"}) {
            m=w.visitMethod(Opcodes.ACC_PUBLIC,method,"()"+COMPONENT,null,null);m.visitCode();
            m.visitFieldInsn(Opcodes.GETSTATIC,name,"header",COMPONENT);m.visitInsn(Opcodes.ARETURN);m.visitMaxs(0,0);m.visitEnd();
        }
        m=w.visitMethod(Opcodes.ACC_PUBLIC,"joinAddress","()Ljava/lang/String;",null,null);m.visitCode();
        if(!staticServer)m.visitVarInsn(Opcodes.ALOAD,0);m.visitFieldInsn(staticServer?Opcodes.GETSTATIC:Opcodes.GETFIELD,name,"server",SERVER);
        m.visitMethodInsn(Opcodes.INVOKEVIRTUAL,SELF+"$LanServer","getAddress","()Ljava/lang/String;",false);
        m.visitInsn(Opcodes.ARETURN);m.visitMaxs(0,0);m.visitEnd();
        w.visitEnd();return w.toByteArray();
    }
    private static byte[] hookSubstitute() {
        ClassWriter w=new ClassWriter(ClassWriter.COMPUTE_MAXS);
        w.visit(Opcodes.V17,Opcodes.ACC_PUBLIC,"minecraft/portable/identity/PortableSteamDiscoveryHooks",null,"java/lang/Object",null);
        var m=w.visitMethod(Opcodes.ACC_PUBLIC|Opcodes.ACC_STATIC,"worldTitle","(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;",null,null);
        m.visitCode();m.visitVarInsn(Opcodes.ALOAD,0);m.visitVarInsn(Opcodes.ALOAD,1);
        m.visitMethodInsn(Opcodes.INVOKESTATIC,SELF,"substituteHeader","(Ljava/lang/Object;Ljava/lang/Object;)Ljava/lang/Object;",false);
        m.visitInsn(Opcodes.ARETURN);m.visitMaxs(0,0);m.visitEnd();
        m=w.visitMethod(Opcodes.ACC_PUBLIC|Opcodes.ACC_STATIC,"displayAddress","(Ljava/lang/String;Ljava/lang/Object;)Ljava/lang/String;",null,null);
        m.visitCode();m.visitVarInsn(Opcodes.ALOAD,0);m.visitVarInsn(Opcodes.ALOAD,1);
        m.visitMethodInsn(Opcodes.INVOKESTATIC,SELF,"substituteAddress","(Ljava/lang/String;Ljava/lang/Object;)Ljava/lang/String;",false);
        m.visitInsn(Opcodes.ARETURN);m.visitMaxs(0,0);m.visitEnd();w.visitEnd();return w.toByteArray();
    }
    private static void actual(String[] args) throws Exception {
        if(args.length!=8)throw new IllegalArgumentException("jar class header server render narration addressMethod output");
        configure(args[1],args[2],args[3],args[4],args[5]);
        byte[] original;
        try(var jar=new ZipFile(args[0]);var in=jar.getInputStream(jar.getEntry(args[1]+".class"))) { original=in.readAllBytes(); }
        ClassNode before=new ClassNode();new ClassReader(original).accept(before,0);
        FieldNode server=before.fields.stream().filter(field->field.name.equals(args[3])).findFirst().orElseThrow();
        System.setProperty(PREFIX+"steamDiscoveryLanServerClasses",Type.getType(server.desc).getInternalName());
        System.setProperty(PREFIX+"steamDiscoveryLanAddressMethods",args[6]);
        byte[] changed=new PortableSteamLanEntryTransformer().transform(null,null,args[1],null,null,original);
        check(changed!=null,"Actual native target unmodified");
        ClassNode n=new ClassNode();new ClassReader(changed).accept(n,0);int calls=0,addresses=0;
        for(MethodNode m:n.methods)for(AbstractInsnNode i:m.instructions)
            if(i instanceof MethodInsnNode c && c.owner.equals("minecraft/portable/identity/PortableSteamDiscoveryHooks")) {
                if(c.name.equals("worldTitle"))calls++;
                if(c.name.equals("displayAddress"))addresses++;
            }
        check(calls==(args[5].isEmpty()?1:2),"Wrong actual mapped call-site count");
        check(addresses==1,"Wrong actual native display-address site count");
        Files.write(Path.of(args[7]),changed);
        System.out.println("ACTUAL_NATIVE_SITE_PASS class="+args[1]+" headerSites="+calls+" addressSites="+addresses+" render="+args[4]+" (no class initialization/game)");
    }
}
