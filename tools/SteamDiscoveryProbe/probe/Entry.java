package probe;
public final class Entry {
    private final String motd,address;
    public Entry(String motd,String address){this.motd=motd;this.address=address;}
    public String getMotd(){return motd;}
    public String getAddress(){return address;}
}
