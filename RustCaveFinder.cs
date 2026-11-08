using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RustCaveFinder {
public class Cave {
    public string Kind, Grid; public float X,Y,Z;
    public string Coordinates { get { return String.Format(System.Globalization.CultureInfo.InvariantCulture,"X {0:0.0}   Y {1:0.0}   Z {2:0.0}",X,Y,Z); } }
    public override string ToString() { return Grid+" · "+Kind; }
}
public class Route { public string Name; public List<PointF> Points=new List<PointF>(); }
public class World {
    public int Size; public string FileName; public byte[] Height,Biome,Splat,Water;
    public List<Cave> Caves=new List<Cave>(); public List<Route> Routes=new List<Route>();
    public Bitmap Image;
    public int Cells { get { return (int)Math.Floor(Size*7.0/1024); } }
    public string Grid(float x,float z) {
        int c=(int)Math.Floor((x+Size/2.0)*Cells/Size),r=(int)Math.Floor((Size/2.0-z)*Cells/Size);
        if(c<0||r<0||c>=Cells||r>=Cells)return "Вне сетки";
        return Column(c)+r;
    }
    public static string Column(int c) { string s=""; for(c++;c>0;c=(c-1)/26)s=(char)('A'+(c-1)%26)+s; return s; }
}
// Reader uses bounded slices; map layers are copied only when needed.
public class Proto {
    public byte[] B; public int P,End,Field,Wire,Start,Length; public ulong Number;
    public Proto(byte[] b,int p,int n){B=b;P=p;End=checked(p+n);if(p<0||End>b.Length)throw new InvalidDataException("Повреждённый блок карты.");}
    public ulong Var() {
        ulong v=0;for(int s=0;s<70;s+=7){if(P>=End)throw new InvalidDataException("Оборванное число.");byte a=B[P++];if(s==63&&a>1)throw new InvalidDataException("Слишком длинное число.");v|=(ulong)(a&127)<<s;if(a<128)return v;}
        throw new InvalidDataException("Слишком длинное число.");
    }
    public bool Next(){
        if(P==End)return false;ulong k=Var();Field=(int)(k>>3);Wire=(int)(k&7);if(Field<=0)throw new InvalidDataException("Неверное поле карты.");
        if(Wire==0){Number=Var();Start=P;Length=0;return true;}
        if(Wire==2){ulong n=Var();if(n>(ulong)(End-P))throw new InvalidDataException("Неверная длина поля.");Length=(int)n;}
        else if(Wire==5)Length=4;else if(Wire==1)Length=8;else throw new InvalidDataException("Неподдерживаемое поле карты.");
        Start=P;if(Length>End-P)throw new InvalidDataException("Оборванное поле карты.");P+=Length;return true;
    }
    public string Text(){return Encoding.UTF8.GetString(B,Start,Length);}
    public Proto Child(){return new Proto(B,Start,Length);}
    public byte[] Copy(){var a=new byte[Length];Buffer.BlockCopy(B,Start,a,0,Length);return a;}
}
public static class MapReader {
    const int MaxDecoded=512*1024*1024;
    public static byte[] DecodeBlock(byte[] b,int length){
        byte[] o=new byte[length];int p=0,q=0;
        while(p<b.Length){
            int t=b[p++],n=t>>4;
            if(n==15){int e;do{if(p>=b.Length)throw new InvalidDataException("Оборванный LZ4.");e=b[p++];n=checked(n+e);}while(e==255);}
            if(n>b.Length-p||n>length-q)throw new InvalidDataException("Неверная длина LZ4.");
            Buffer.BlockCopy(b,p,o,q,n);p+=n;q+=n;if(p==b.Length)break;
            if(p+2>b.Length)throw new InvalidDataException("Оборванный LZ4.");int off=b[p]|(b[p+1]<<8);p+=2;
            if(off<=0||off>q)throw new InvalidDataException("Неверное смещение LZ4.");n=(t&15)+4;
            if((t&15)==15){int e;do{if(p>=b.Length)throw new InvalidDataException("Оборванный LZ4.");e=b[p++];n=checked(n+e);}while(e==255);}
            if(n>length-q)throw new InvalidDataException("Слишком большой блок LZ4.");
            // Repeated matches may overlap; copying in growing pieces preserves LZ4 semantics.
            while(n>0){int take=Math.Min(n,off);Buffer.BlockCopy(o,q-off,o,q,take);q+=take;n-=take;off+=take;}
        }
        if(q!=length)throw new InvalidDataException("Размер распакованного блока не совпал.");return o;
    }
    static ulong ReadVar(Stream s){ulong v=0;for(int k=0;k<70;k+=7){int b=s.ReadByte();if(b<0)throw new InvalidDataException("Файл карты обрезан.");if(k==63&&b>1)throw new InvalidDataException("Неверный заголовок LZ4.");v|=(ulong)(b&127)<<k;if(b<128)return v;}throw new InvalidDataException("Неверное число.");}
    static byte[] Unpack(string path){
        using(var f=File.OpenRead(path))using(var r=new BinaryReader(f))using(var dst=new MemoryStream()){
            if(f.Length<12||f.Length>256L*1024*1024)throw new InvalidDataException("Неверный размер файла .map (предел 256 МБ).");
            uint version=r.ReadUInt32();if(version!=10)throw new InvalidDataException("Версия карты "+version+" пока не поддерживается (ожидалась 10).");r.ReadInt64();
            while(f.Position<f.Length){ulong flags=ReadVar(f),n=ReadVar(f),c=(flags&1)!=0?ReadVar(f):n;
                if((flags&~3UL)!=0||n>16*1024*1024||c>n||dst.Length+(long)n>MaxDecoded)throw new InvalidDataException("Неподдерживаемый или слишком большой блок карты.");
                byte[] block=r.ReadBytes((int)c);if(block.Length!=(int)c)throw new InvalidDataException("Файл карты обрезан.");
                byte[] decoded=(flags&1)!=0?DecodeBlock(block,(int)n):block;dst.Write(decoded,0,decoded.Length);
            }
            return dst.ToArray();
        }
    }
    static Dictionary<uint,string> CaveIds(){
        var names=new Dictionary<string,string>{{"cave_small_easy","Маленькая · лёгкая"},{"cave_small_medium","Маленькая · средняя"},{"cave_small_hard","Маленькая · сложная"},{"cave_medium_easy","Средняя · лёгкая"},{"cave_medium_medium","Средняя · средняя"},{"cave_medium_hard","Средняя · сложная"},{"cave_large_medium","Большая · средняя"},{"cave_large_hard","Большая · сложная"},{"cave_large_sewers_hard","Большая · канализация"}};
        var ids=new Dictionary<uint,string>();using(var md=MD5.Create())foreach(var n in names){var b=md.ComputeHash(Encoding.UTF8.GetBytes("assets/bundled/prefabs/autospawn/monument/cave/"+n.Key+".prefab"));ids.Add(BitConverter.ToUInt32(b,0),n.Value);}return ids;
    }
    static float[] Vector(Proto p){float[] a=new float[3];while(p.Next())if(p.Field>=1&&p.Field<=3&&p.Wire==5){float v=BitConverter.ToSingle(p.B,p.Start);if(float.IsNaN(v)||float.IsInfinity(v))throw new InvalidDataException("Неверная позиция объекта.");a[p.Field-1]=v;}return a;}
    public static World Load(string path,Action<string> progress){
        progress("Распаковка карты…");byte[] data=Unpack(path);var w=new World();w.FileName=Path.GetFileName(path);var ids=CaveIds();byte[] terrain=null;
        var p=new Proto(data,0,data.Length);progress("Поиск пещер и чтение рельефа…");
        while(p.Next()){
            if(p.Field==1&&p.Wire==0)w.Size=checked((int)p.Number);
            if(p.Field==2&&p.Wire==2){var m=p.Child();string name="";int start=0,len=0;while(m.Next()){if(m.Field==1&&m.Wire==2)name=m.Text();if(m.Field==2&&m.Wire==2){start=m.Start;len=m.Length;}}
                if(name=="height"||name=="biome"||name=="splat"||name=="terrain"||name=="water"){
                    byte[] a=new byte[len];Buffer.BlockCopy(data,start,a,0,len);
                    if(name=="height")w.Height=a;if(name=="biome")w.Biome=a;if(name=="splat")w.Splat=a;if(name=="terrain")terrain=a;if(name=="water")w.Water=a;
                }
            }
            if(p.Field==3&&p.Wire==2){var f=p.Child();uint id=0;float[] pos=null;while(f.Next()){if(f.Field==2&&f.Wire==0)id=checked((uint)f.Number);if(f.Field==3&&f.Wire==2)pos=Vector(f.Child());}
                string kind;if(ids.TryGetValue(id,out kind)&&pos!=null)w.Caves.Add(new Cave{Kind=kind,X=pos[0],Y=pos[1],Z=pos[2]});
            }
            if(p.Field==4&&p.Wire==2){var f=p.Child();var route=new Route();while(f.Next()){if(f.Field==1&&f.Wire==2)route.Name=f.Text();if(f.Field==15&&f.Wire==2){float[] v=Vector(f.Child());if(route.Points.Count<100000)route.Points.Add(new PointF(v[0],v[2]));}}
                if(route.Name!=null&&route.Points.Count>1&&(route.Name.StartsWith("Road",StringComparison.OrdinalIgnoreCase)||route.Name.StartsWith("River",StringComparison.OrdinalIgnoreCase)||route.Name.StartsWith("Rail",StringComparison.OrdinalIgnoreCase)))w.Routes.Add(route);
            }
        }
        if(w.Size<100||w.Size>10000)throw new InvalidDataException("Не удалось определить размер карты.");if(terrain!=null)w.Height=terrain;
        foreach(var c in w.Caves)c.Grid=w.Grid(c.X,c.Z);
        w.Caves=w.Caves.OrderByDescending(c=>c.Z).ThenBy(c=>c.X).ToList();progress("Рисование карты…");w.Image=TerrainRenderer.Render(w,1536);
        w.Height=null;w.Biome=null;w.Splat=null;w.Water=null;return w;
    }
}
public static class TerrainRenderer {
    static int Side(byte[] a,int channels){if(a==null)return 0;int n=(int)Math.Round(Math.Sqrt(a.Length/(double)channels));return n*n*channels==a.Length?n:0;}
    static int Index(double x,double z,int n){return ((int)Math.Round(z*(n-1))*n+(int)Math.Round(x*(n-1)));}
    static double H(byte[] b,int n,double x,double z,double scale){int k=Index(x,z,n)*2;return (b[k]|b[k+1]<<8)*1000.0/scale-500;}
    static int Clamp(double x){return Math.Max(0,Math.Min(255,(int)x));}
    public static Bitmap Render(World w,int size){
        int hn=Side(w.Height,2),bn=Side(w.Biome,5),sn=Side(w.Splat,8),wn=Side(w.Water,2);if(hn<2)throw new InvalidDataException("В карте нет корректного слоя высот.");
        // Unity terrain maps use 15-bit heights; accept full-range 16-bit exports too.
        int maximum=0;for(int k=0;k<w.Height.Length;k+=2)maximum=Math.Max(maximum,w.Height[k]|w.Height[k+1]<<8);double scale=maximum>32767?65535:32767;
        var bmp=new Bitmap(size,size,PixelFormat.Format32bppArgb);byte[] pixels=new byte[size*size*4];
        double[,] colors={{183,161,111},{110,137,98},{126,133,109},{211,219,217},{74,115,79}};
        for(int y=0;y<size;y++)for(int x=0;x<size;x++){
            double u=x/(double)(size-1),v=1-y/(double)(size-1),h=H(w.Height,hn,u,v,scale),rr,gg,bb;
            double water=wn>0?H(w.Water,wn,u,v,scale):-500;
            if(h<0||water>h+.3){double t=Math.Min(1,Math.Max(0,Math.Max(0,water)-h)/90);rr=48-25*t;gg=92-37*t;bb=109-38*t;}
            else {
                rr=110;gg=137;bb=98;
                if(bn>0){int k=Index(u,v,bn);double sum=0;rr=gg=bb=0;for(int j=0;j<5;j++){double weight=w.Biome[k+j*bn*bn];sum+=weight;rr+=weight*colors[j,0];gg+=weight*colors[j,1];bb+=weight*colors[j,2];}if(sum>0){rr/=sum;gg/=sum;bb/=sum;}else{rr=110;gg=137;bb=98;}}
                if(sn>0){int k=Index(u,v,sn),plane=sn*sn;double snow=w.Splat[k+plane]/255.0,sand=w.Splat[k+2*plane]/255.0,rock=w.Splat[k+3*plane]/255.0;double a=Math.Min(.82,snow*.85);rr=rr*(1-a)+223*a;gg=gg*(1-a)+229*a;bb=bb*(1-a)+226*a;a=sand*.55;rr=rr*(1-a)+192*a;gg=gg*(1-a)+174*a;bb=bb*(1-a)+128*a;a=rock*.42;rr=rr*(1-a)+127*a;gg=gg*(1-a)+130*a;bb=bb*(1-a)+124*a;}
                double d=1.0/(size-1),hx=H(w.Height,hn,Math.Min(1,u+d),v,scale)-H(w.Height,hn,Math.Max(0,u-d),v,scale),hz=H(w.Height,hn,u,Math.Min(1,v+d),scale)-H(w.Height,hn,u,Math.Max(0,v-d),scale);
                double shade=Math.Max(.65,Math.Min(1.23,1+(-hx+hz)*.035));rr*=shade;gg*=shade;bb*=shade;
                if(h<2){rr=rr*.7+186*.3;gg=gg*.7+175*.3;bb=bb*.7+130*.3;}
            }
            int i=(y*size+x)*4;pixels[i]=(byte)Clamp(bb);pixels[i+1]=(byte)Clamp(gg);pixels[i+2]=(byte)Clamp(rr);pixels[i+3]=255;
        }
        var bd=bmp.LockBits(new Rectangle(0,0,size,size),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);Marshal.Copy(pixels,0,bd.Scan0,pixels.Length);bmp.UnlockBits(bd);
        using(var g=Graphics.FromImage(bmp)){g.SmoothingMode=SmoothingMode.AntiAlias;foreach(var r in w.Routes){bool river=r.Name.StartsWith("River",StringComparison.OrdinalIgnoreCase),rail=r.Name.StartsWith("Rail",StringComparison.OrdinalIgnoreCase);var pts=r.Points.Select(a=>new PointF((a.X/w.Size+.5f)*size,(.5f-a.Y/w.Size)*size)).ToArray();using(var pen=new Pen(river?Color.FromArgb(160,63,112,133):rail?Color.FromArgb(175,57,67,66):Color.FromArgb(180,207,195,161),river?2.4f:rail?1.3f:2.2f))g.DrawLines(pen,pts);}}
        return bmp;
    }
}
public class MapView:Control {
    public World World; public int Selected=-1;public event Action<int> CaveSelected;public event Action<string> PositionChanged;
    float zoom=1,panX,panY;Point drag;bool dragging;public bool GridVisible=true;
    public MapView(){DoubleBuffered=true;BackColor=Color.FromArgb(20,28,32);Cursor=Cursors.Hand;SetStyle(ControlStyles.ResizeRedraw,true);}
    RectangleF BoundsFor(int width,int height){float s=Math.Max(1,Math.Min(width-64,height-64))*zoom;return new RectangleF((width-s)/2+panX,(height-s)/2+panY,s,s);}
    public void Fit(){zoom=1;panX=panY=0;Invalidate();}
    public void FocusCave(int i){Selected=i;if(World==null||i<0)return;zoom=2.4f;var c=World.Caves[i];float s=Math.Min(Width-64,Height-64)*zoom;panX=-c.X/World.Size*s;panY=c.Z/World.Size*s;Invalidate();}
    public void ChangeZoom(float factor){zoom=Math.Max(.6f,Math.Min(12,zoom*factor));Invalidate();}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);if(World==null){TextRenderer.DrawText(e.Graphics,"Открой файл .map — здесь появятся пещеры",Font,ClientRectangle,Color.FromArgb(151,168,174),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);return;}Draw(e.Graphics,Width,Height,true);}
    public void Draw(Graphics g,int width,int height,bool viewport){
        g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;RectangleF r=viewport?BoundsFor(width,height):new RectangleF(44,44,width-88,height-88);
        g.InterpolationMode=InterpolationMode.HighQualityBilinear;g.DrawImage(World.Image,r);g.SetClip(r);
        int cells=World.Cells;float step=r.Width/cells;
        using(var font=new Font("Segoe UI",Math.Max(7,Math.Min(10,step*.2f)),FontStyle.Bold))using(var line=new Pen(Color.FromArgb(53,235,244,239),1)){
            if(GridVisible){for(int i=0;i<=cells;i++){float x=r.X+i*step,y=r.Y+i*step;g.DrawLine(line,x,r.Y,x,r.Bottom);g.DrawLine(line,r.X,y,r.Right,y);}
                if(step>22)for(int y=0;y<cells;y++)for(int x=0;x<cells;x++)Shadow(g,World.Column(x)+y,font,r.X+x*step+3,r.Y+y*step+2,Color.FromArgb(215,240,244,237));}
        }
        for(int i=0;i<World.Caves.Count;i++){
            var c=World.Caves[i];float x=r.X+(c.X/World.Size+.5f)*r.Width,y=r.Y+(.5f-c.Z/World.Size)*r.Height;bool selected=i==Selected;
            float radius=Math.Max(14,35*r.Width/World.Size);Color accent=selected?Color.FromArgb(255,221,123):Color.FromArgb(255,115,89);
            using(var p=new Pen(Color.FromArgb(210,20,26,28),6))g.DrawEllipse(p,x-radius,y-radius,radius*2,radius*2);
            using(var p=new Pen(accent,selected?3.5f:2.5f))g.DrawEllipse(p,x-radius,y-radius,radius*2,radius*2);
            using(var b=new SolidBrush(accent))g.FillEllipse(b,x-3,y-3,6,6);
            using(var f=new Font("Segoe UI",10,FontStyle.Bold))Shadow(g,(i+1)+" · "+c.Grid,f,x+radius+5,y-10,accent);
        }
        g.ResetClip();if(GridVisible)using(var f=new Font("Segoe UI",8,FontStyle.Bold))using(var b=new SolidBrush(Color.FromArgb(180,199,202)))for(int i=0;i<cells;i++){g.DrawString(World.Column(i),f,b,r.X+(i+.35f)*step,r.Y-15);g.DrawString(i.ToString(),f,b,r.X-24,r.Y+(i+.25f)*step);}
        using(var pen=new Pen(Color.FromArgb(75,104,113)))g.DrawRectangle(pen,r.X,r.Y,r.Width,r.Height);
        using(var f=new Font("Segoe UI",9,FontStyle.Bold))Shadow(g,"СЕВЕР ↑",f,r.Right-75,r.Y+6,Color.FromArgb(220,234,238));
    }
    static void Shadow(Graphics g,string text,Font f,float x,float y,Color color){using(var black=new SolidBrush(Color.FromArgb(220,13,22,25)))using(var b=new SolidBrush(color)){g.DrawString(text,f,black,x+1,y+1);g.DrawString(text,f,b,x,y);}}
    protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button==MouseButtons.Left){drag=e.Location;dragging=true;Capture=true;}}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;}
    protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging){panX+=e.X-drag.X;panY+=e.Y-drag.Y;drag=e.Location;Invalidate();}if(World!=null){var r=BoundsFor(Width,Height);if(r.Contains(e.Location)){float x=((e.X-r.X)/r.Width-.5f)*World.Size,z=(.5f-(e.Y-r.Y)/r.Height)*World.Size;if(PositionChanged!=null)PositionChanged(World.Grid(x,z)+String.Format("   X {0:0}   Z {1:0}",x,z));}}}
    protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);if(World==null)return;var before=BoundsFor(Width,Height);float old=zoom;zoom=Math.Max(.6f,Math.Min(12,zoom*(e.Delta>0?1.2f:1/1.2f)));float ratio=zoom/old;panX=(e.X-Width/2f)*(1-ratio)+panX*ratio;panY=(e.Y-Height/2f)*(1-ratio)+panY*ratio;Invalidate();}
    protected override void OnMouseDoubleClick(MouseEventArgs e){base.OnMouseDoubleClick(e);if(World==null)return;var r=BoundsFor(Width,Height);for(int i=0;i<World.Caves.Count;i++){var c=World.Caves[i];float x=r.X+(c.X/World.Size+.5f)*r.Width,y=r.Y+(.5f-c.Z/World.Size)*r.Height;if(Math.Abs(e.X-x)<25&&Math.Abs(e.Y-y)<25){if(CaveSelected!=null)CaveSelected(i);return;}}Fit();}
}
public class FileItem { public string Path; public override string ToString(){return System.IO.Path.GetFileName(Path)+"  ·  "+File.GetLastWriteTime(Path).ToString("dd.MM HH:mm");} }
public class MainForm:Form {
    MapView map=new MapView();ListBox list=new ListBox();Label status=new Label(),detail=new Label(),summary=new Label();ComboBox files=new ComboBox();Button open,savePng,saveCsv,copy;bool loading;World world;
    static Color Bg=Color.FromArgb(18,25,29),PanelBg=Color.FromArgb(27,37,42),Ink=Color.FromArgb(230,238,238),Muted=Color.FromArgb(155,175,181),Accent=Color.FromArgb(255,115,89);
    public MainForm(){
        Text="Rust Cave Finder";ClientSize=new Size(1280,860);MinimumSize=new Size(950,650);StartPosition=FormStartPosition.CenterScreen;BackColor=Bg;ForeColor=Ink;Font=new Font("Segoe UI",10);AllowDrop=true;
        var header=new Panel{Dock=DockStyle.Top,Height=82,BackColor=Bg};header.Controls.Add(new Label{Text="RUST / CAVE FINDER",Font=new Font("Segoe UI",21,FontStyle.Bold),ForeColor=Ink,AutoSize=true,Location=new Point(22,12)});
        header.Controls.Add(new Label{Text="Пещеры на твоей карте. Локально, без админки.",ForeColor=Muted,AutoSize=true,Location=new Point(25,51)});
        var toolbar=new FlowLayoutPanel{Dock=DockStyle.Top,Height=52,Padding=new Padding(16,5,10,5),BackColor=PanelBg,WrapContents=false};toolbar.Resize+=delegate{files.Width=Math.Max(180,Math.Min(440,toolbar.Width-590));};
        open=Button("Открыть .map",delegate{Choose();});open.BackColor=Accent;open.ForeColor=Bg;toolbar.Controls.Add(open);
        files.Width=440;files.DropDownWidth=880;files.DropDownStyle=ComboBoxStyle.DropDownList;files.BackColor=Bg;files.ForeColor=Ink;files.Margin=new Padding(8,6,8,0);files.SelectedIndexChanged+=delegate{if(files.SelectedItem!=null&&!loading)LoadMap(((FileItem)files.SelectedItem).Path);};toolbar.Controls.Add(files);
        toolbar.Controls.Add(Button("Обновить",delegate{Discover();}));savePng=Button("Карта PNG",delegate{ExportPng();});saveCsv=Button("Координаты CSV",delegate{ExportCsv();});toolbar.Controls.Add(savePng);toolbar.Controls.Add(saveCsv);
        var bottom=new Panel{Dock=DockStyle.Bottom,Height=34,BackColor=PanelBg};status.Dock=DockStyle.Fill;status.Text="Выбери файл карты";status.Padding=new Padding(20,7,0,0);status.ForeColor=Muted;bottom.Controls.Add(status);
        var split=new SplitContainer{Dock=DockStyle.Fill,BackColor=Bg,SplitterWidth=6,FixedPanel=FixedPanel.Panel2};split.Panel1.Controls.Add(map);map.Dock=DockStyle.Fill;map.Font=Font;
        var controls=new FlowLayoutPanel{Dock=DockStyle.Top,Height=45,Padding=new Padding(12,3,0,0),BackColor=Bg};controls.Controls.Add(Button("+",delegate{map.ChangeZoom(1.3f);},40));controls.Controls.Add(Button("−",delegate{map.ChangeZoom(1/1.3f);},40));controls.Controls.Add(Button("Вся карта",delegate{map.Fit();}));
        var grid=new CheckBox{Text="Сетка",Checked=true,AutoSize=true,Margin=new Padding(16,9,0,0)};grid.CheckedChanged+=delegate{map.GridVisible=grid.Checked;map.Invalidate();};controls.Controls.Add(grid);split.Panel1.Controls.Add(controls);
        var sidebar=split.Panel2;sidebar.BackColor=PanelBg;summary.Dock=DockStyle.Top;summary.Height=76;summary.Padding=new Padding(16,16,8,0);summary.Text="ПЕЩЕРЫ\nОжидание карты";summary.Font=new Font("Segoe UI",12,FontStyle.Bold);
        list.Dock=DockStyle.Fill;list.BorderStyle=BorderStyle.None;list.BackColor=PanelBg;list.ForeColor=Ink;list.DrawMode=DrawMode.OwnerDrawFixed;list.ItemHeight=76;list.DrawItem+=DrawCave;list.SelectedIndexChanged+=delegate{SelectCave(list.SelectedIndex);};list.DoubleClick+=delegate{if(list.SelectedIndex>=0)map.FocusCave(list.SelectedIndex);};
        var info=new Panel{Dock=DockStyle.Bottom,Height=187,Padding=new Padding(15)};detail.Dock=DockStyle.Fill;detail.ForeColor=Muted;detail.Text="Круг обозначает позицию объекта пещеры. Вход может быть рядом.\n\nКолесо — масштаб. Перетаскивание — перемещение.";copy=Button("Копировать координаты",delegate{Copy();},265);copy.Dock=DockStyle.Bottom;info.Controls.Add(detail);info.Controls.Add(copy);sidebar.Controls.Add(list);sidebar.Controls.Add(info);sidebar.Controls.Add(summary);
        Controls.Add(split);Controls.Add(toolbar);Controls.Add(header);Controls.Add(bottom);Shown+=delegate{split.SplitterDistance=Math.Max(520,split.Width-315);};
        map.CaveSelected+=delegate(int i){list.SelectedIndex=i;};map.PositionChanged+=delegate(string s){if(!loading)status.Text=s;};
        DragEnter+=delegate(object s,DragEventArgs e){if(e.Data.GetDataPresent(DataFormats.FileDrop))e.Effect=DragDropEffects.Copy;};DragDrop+=delegate(object s,DragEventArgs e){var a=(string[])e.Data.GetData(DataFormats.FileDrop);if(a.Length>0)LoadMap(a[0]);};
        FormClosed+=delegate{if(world!=null&&world.Image!=null)world.Image.Dispose();};SetButtons();
    }
    Button Button(string text,EventHandler action,int width=125){var b=new Button{Text=text,Width=width,Height=34,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(43,58,65),ForeColor=Ink,Margin=new Padding(4,2,4,2),Cursor=Cursors.Hand};b.FlatAppearance.BorderSize=0;b.Click+=action;return b;}
    void SetButtons(){savePng.Enabled=saveCsv.Enabled=world!=null&&!loading;copy.Enabled=world!=null&&list.SelectedIndex>=0&&!loading;open.Enabled=files.Enabled=!loading;}
    void DrawCave(object sender,DrawItemEventArgs e){if(e.Index<0||world==null||e.Index>=world.Caves.Count)return;var c=world.Caves[e.Index];bool selected=(e.State&DrawItemState.Selected)!=0;using(var b=new SolidBrush(selected?Color.FromArgb(54,67,71):PanelBg))e.Graphics.FillRectangle(b,e.Bounds);if(selected)using(var b=new SolidBrush(Accent))e.Graphics.FillRectangle(b,e.Bounds.X,e.Bounds.Y,4,e.Bounds.Height);using(var f=new Font("Segoe UI",12,FontStyle.Bold))using(var b=new SolidBrush(Accent))e.Graphics.DrawString((e.Index+1)+"   "+c.Grid,f,b,e.Bounds.X+16,e.Bounds.Y+8);using(var b=new SolidBrush(Ink))e.Graphics.DrawString(c.Kind,Font,b,e.Bounds.X+16,e.Bounds.Y+31);using(var f=new Font("Segoe UI",8))using(var b=new SolidBrush(Muted))e.Graphics.DrawString(String.Format("X {0:0}   Z {1:0}",c.X,c.Z),f,b,e.Bounds.X+16,e.Bounds.Y+54);}
    void SelectCave(int i){if(world==null||i<0||i>=world.Caves.Count)return;map.Selected=i;map.Invalidate();var c=world.Caves[i];detail.Text=c.Grid+" · "+c.Kind+"\n\n"+c.Coordinates+"\n\nЭто позиция пещеры. Вход может быть в соседней клетке.";SetButtons();}
    void Choose(){using(var d=new OpenFileDialog{Filter="Карты Rust (*.map)|*.map|Все файлы (*.*)|*.*",Title="Открыть карту Rust"})if(d.ShowDialog()==DialogResult.OK)LoadMap(d.FileName);}
    public async void LoadMap(string path){
        if(loading)return;loading=true;SetButtons();status.Text="Чтение карты…";
        try{var result=await Task.Run(()=>MapReader.Load(path,s=>{if(IsHandleCreated&&!IsDisposed)BeginInvoke((Action)(()=>status.Text=s));}));var old=world;world=result;map.World=world;map.Selected=-1;map.Fit();list.Items.Clear();foreach(var c in world.Caves)list.Items.Add(c);summary.Text="НАЙДЕНО ПЕЩЕР: "+world.Caves.Count+"\nКарта "+world.Size+" × "+world.Size+" м";Text="Rust Cave Finder — "+world.FileName;status.Text=world.FileName+"  ·  колесо: масштаб, перетаскивание: перемещение";if(old!=null)old.Image.Dispose();if(list.Items.Count>0)list.SelectedIndex=0;else detail.Text="Стандартные пещеры не найдены. Пещеры, собранные автором из отдельных деталей, автоматически не распознаются.";}
        catch(Exception ex){MessageBox.Show(this,"Не удалось открыть карту.\n\n"+ex.Message,"Rust Cave Finder",MessageBoxButtons.OK,MessageBoxIcon.Warning);status.Text="Не удалось прочитать файл";}
        finally{loading=false;SetButtons();}
    }
    public void Discover(){
        var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);string steam=null;try{using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))if(k!=null)steam=k.GetValue("SteamPath") as string;}catch{}
        if(steam==null)steam=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam");roots.Add(steam);
        try{string vdf=File.ReadAllText(Path.Combine(steam,"steamapps","libraryfolders.vdf"));foreach(Match m in Regex.Matches(vdf,"\"path\"\\s+\"([^\"]+)\""))roots.Add(m.Groups[1].Value.Replace(@"\\",@"\"));}catch{}
        var found=new List<string>();foreach(string root in roots){try{string dir=Path.Combine(root,"steamapps","common","Rust","maps");if(Directory.Exists(dir))found.AddRange(Directory.GetFiles(dir,"*.map"));}catch{}}
        files.Items.Clear();foreach(string p in found.OrderByDescending(File.GetLastWriteTime))files.Items.Add(new FileItem{Path=p});
        if(files.Items.Count>0)files.SelectedIndex=0;else status.Text="Карты не найдены автоматически. Нажми «Открыть .map».";
    }
    void Copy(){if(world==null||list.SelectedIndex<0)return;var c=world.Caves[list.SelectedIndex];try{Clipboard.SetText(c.Grid+" · "+c.Kind+"\r\n"+c.Coordinates);status.Text="Координаты скопированы";}catch(Exception ex){MessageBox.Show(ex.Message);}}
    public static void WriteCsv(World w,string path){using(var sw=new StreamWriter(path,false,new UTF8Encoding(true))){sw.WriteLine("number;grid;type;x;y;z");for(int i=0;i<w.Caves.Count;i++){var c=w.Caves[i];sw.WriteLine(String.Format(System.Globalization.CultureInfo.InvariantCulture,"{0};{1};{2};{3:0.00};{4:0.00};{5:0.00}",i+1,c.Grid,c.Kind,c.X,c.Y,c.Z));}}}
    void ExportCsv(){using(var d=new SaveFileDialog{Filter="Таблица CSV|*.csv",FileName=Path.GetFileNameWithoutExtension(world.FileName)+"-caves.csv"})if(d.ShowDialog()==DialogResult.OK){try{WriteCsv(world,d.FileName);status.Text="CSV сохранён";}catch(Exception ex){MessageBox.Show(ex.Message);}}}
    void ExportPng(){using(var d=new SaveFileDialog{Filter="Изображение PNG|*.png",FileName=Path.GetFileNameWithoutExtension(world.FileName)+"-caves.png"})if(d.ShowDialog()==DialogResult.OK){try{using(var b=new Bitmap(1800,1800))using(var g=Graphics.FromImage(b)){map.Draw(g,1800,1800,false);b.Save(d.FileName,ImageFormat.Png);}status.Text="Карта PNG сохранена";}catch(Exception ex){MessageBox.Show(ex.Message);}}}
    public void SetWorldForVerification(World w){world=w;map.World=w;summary.Text="НАЙДЕНО ПЕЩЕР: "+w.Caves.Count+"\nКарта "+w.Size+" × "+w.Size+" м";foreach(var c in w.Caves)list.Items.Add(c);if(list.Items.Count>0)list.SelectedIndex=0;status.Text=w.FileName;SetButtons();}
}
static class Program {
    [STAThread] static void Main(string[] args){
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        if(args.Length>0&&args[0]=="--verify"){
            try{SelfTest.Run();var w=MapReader.Load(args[1],Console.WriteLine);string dir=args[2];Directory.CreateDirectory(dir);MainForm.WriteCsv(w,Path.Combine(dir,"verified-caves.csv"));
                if(w.Caves.Count!=5||!w.Caves.Any(c=>c.Grid=="D7")||!w.Caves.Any(c=>c.Grid=="W17"))throw new Exception("Unexpected fixture cave positions");
                int land=0,sea=0;for(int y=0;y<w.Image.Height;y+=16)for(int x=0;x<w.Image.Width;x+=16){Color c=w.Image.GetPixel(x,y);if(c.R>70&&c.G>70)land++;if(c.B>c.R&&c.G>c.R)sea++;}if(land<1000||sea<1000)throw new Exception("Terrain land/sea rendering failed");
                using(var form=new MainForm()){form.Show();form.SetWorldForVerification(w);Application.DoEvents();using(var b=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(Path.Combine(dir,"app-preview.png"));}
                    var canvas=new MapView{World=w};using(var b=new Bitmap(1800,1800))using(var g=Graphics.FromImage(b)){canvas.Draw(g,1800,1800,false);b.Save(Path.Combine(dir,"rustburg-caves.png"));}canvas.Dispose();}
                Console.WriteLine("PASS: real map, 5 caves, grid, CSV and window rendering.");Environment.Exit(0);
            }catch(Exception e){Console.WriteLine(e);Environment.Exit(1);}return;
        }
        var app=new MainForm();app.Shown+=delegate{if(args.Length>0)app.LoadMap(args[0]);else app.Discover();};Application.Run(app);
    }
}
static class SelfTest {
    static void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);}
    public static void Run(){
        Check(Encoding.ASCII.GetString(MapReader.DecodeBlock(new byte[]{0x50,104,101,108,108,111},5))=="hello","LZ4 literals");
        Check(Encoding.ASCII.GetString(MapReader.DecodeBlock(new byte[]{0x14,97,1,0,0x50,98,99,100,101,102},14))=="aaaaaaaaabcdef","LZ4 overlap");
        bool failed=false;try{MapReader.DecodeBlock(new byte[]{0,0,0},4);}catch(InvalidDataException){failed=true;}Check(failed,"reject zero offset");
        failed=false;try{MapReader.DecodeBlock(new byte[]{0xF0},1);}catch(InvalidDataException){failed=true;}Check(failed,"reject truncated LZ4");
        failed=false;try{var p=new Proto(new byte[]{10,8,1},0,3);p.Next();}catch(InvalidDataException){failed=true;}Check(failed,"reject truncated protobuf");
        var w=new World{Size=4250};Check(w.Cells==29&&w.Grid(-1672.87f,1054.82f)=="D7"&&w.Grid(1192.99f,-511.32f)=="W17","grid coordinates");Check(World.Column(25)=="Z"&&World.Column(26)=="AA","grid letters");
        Console.WriteLine("PASS: decoder boundary checks and grid.");
    }
}
}
