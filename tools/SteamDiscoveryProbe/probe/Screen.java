package probe;
public class Screen {
    public final LanList lanList=new LanList();
    public int vanillaJoins,initializations;
    public void init(){initializations++;}
    public void removed(){}
    public void join(Data data){vanillaJoins++;}
}
