using System.Buffers;
using System.Runtime.InteropServices;
namespace Lenscast;

internal sealed record VideoFrame(int Width,int Height,byte[] Pixels,long PhoneUs,string Decoder):IDisposable {
 public void Dispose()=>ArrayPool<byte>.Shared.Return(Pixels);
}

// Direct, narrow MF/D3D interop. Vtable slots, GUIDs and signatures are from
// the official WinSDK mfobjects.h/mftransform.h/mfapi.h/d3d10.h; see API_AUDIT.
// No third-party decoder, native shim, DirectShow, audio or file recording.
internal sealed unsafe class VideoDecoder:IDisposable {
 [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint mode);
 [DllImport("ole32.dll")] private static extern void CoUninitialize();
 [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid,nint outer,uint context,in Guid iid,out nint instance);
 [StructLayout(LayoutKind.Sequential)] private struct Registration { internal Guid Major,Subtype; }
 [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category,uint flags,in Registration input,in Registration output,out nint activations,out uint count);
 [DllImport("mfplat.dll")] private static extern int MFStartup(uint version,uint flags);
 [DllImport("mfplat.dll")] private static extern int MFShutdown();
 [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
 [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
 [DllImport("mfplat.dll")] private static extern int MFCreateAlignedMemoryBuffer(uint size,uint alignment,out nint buffer);
 [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint size,out nint buffer);
 [DllImport("mfplat.dll")] private static extern int MFCreateDXGIDeviceManager(out uint token,out nint manager);
 [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter,uint driver,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
 private static readonly Guid DecoderClsid=new("62ce7e72-4c71-4d20-b15d-452831a87d9d"), TransformIid=new("bf94c121-5b05-4e6f-8000-ba598961414d");
 private static readonly Guid Major=new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype=new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"), Video=new("73646976-0000-0010-8000-00aa00389b71"), H264=new("34363248-0000-0010-8000-00aa00389b71"), Nv12=new("3231564e-0000-0010-8000-00aa00389b71");
 private static readonly Guid FrameSize=new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate=new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"), Interlace=new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), Stride=new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), LowLatency=new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
 private static readonly Guid Aware=new("206b4fc8-fcf9-4c51-afe3-9764369e33a0"), MultiThreadIid=new("9b7e4e00-342c-4106-a19f-4f2704f689f0"), Buffer2DIid=new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
 private const int NeedMore=unchecked((int)0xc00d6d72), StreamChange=unchecked((int)0xc00d6d61), NotAccepting=unchecked((int)0xc00d36b5);
 [StructLayout(LayoutKind.Sequential)] private struct OutputInfo { internal uint Flags,Size,Alignment; }
 [StructLayout(LayoutKind.Sequential)] private struct Output { internal uint Id; internal nint Sample; internal uint Status; internal nint Events; }
 private nint transform,device,context,manager;
 private bool com,mf;
 private int width,height,stride;
 internal bool UsingDevice { get; private set; }
 private static void* Fn(nint p,int slot)=>(void*)(*(nint**)p)[slot];
 private static void Check(int hr) { if(hr<0) Marshal.ThrowExceptionForHR(hr); }
 private static void Release(ref nint p) { var value=p; p=0; if(value!=0) ((delegate* unmanaged[Stdcall]<nint,uint>)Fn(value,2))(value); }
 private static nint Query(nint p,Guid id) { nint result=0; return ((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Fn(p,0))(p,&id,&result)>=0?result:0; }
 private static void SetGuid(nint p,Guid key,Guid value)=>Check(((delegate* unmanaged[Stdcall]<nint,Guid*,Guid*,int>)Fn(p,24))(p,&key,&value));
 private static void Set32(nint p,Guid key,uint value)=>Check(((delegate* unmanaged[Stdcall]<nint,Guid*,uint,int>)Fn(p,21))(p,&key,value));
 private static void Set64(nint p,Guid key,ulong value)=>Check(((delegate* unmanaged[Stdcall]<nint,Guid*,ulong,int>)Fn(p,22))(p,&key,value));
 private static uint Get32(nint p,Guid key,uint fallback=0) { uint value=0; return ((delegate* unmanaged[Stdcall]<nint,Guid*,uint*,int>)Fn(p,7))(p,&key,&value)>=0?value:fallback; }
 private static ulong Get64(nint p,Guid key) { ulong value=0; Check(((delegate* unmanaged[Stdcall]<nint,Guid*,ulong*,int>)Fn(p,8))(p,&key,&value)); return value; }
 private void Message(uint message,nint value=0)=>Check(((delegate* unmanaged[Stdcall]<nint,uint,nint,int>)Fn(transform,23))(transform,message,value));
 internal VideoDecoder(int w,int h,bool software=false,int codec=1) {
  try {
   Check(CoInitializeEx(0,0)); com=true;
   Check(MFStartup(0x20070,0)); mf=true;
   if(codec==1) Check(CoCreateInstance(DecoderClsid,0,1,TransformIid,out transform));
   else {
    var input=new Registration {Major=Video,Subtype=new Guid("43564548-0000-0010-8000-00aa00389b71")};
    var output=new Registration {Major=Video,Subtype=Nv12};
    Check(MFTEnumEx(new Guid("d6c02d4b-6833-45b4-971a-05a4b04bab91"),0x51,input,output,out var activations,out var count));
    try {
     if(count==0) throw new NotSupportedException("Декодировщик HEVC не установлен");
     for(int i=0;i<count && transform==0;i++) {
      nint activate=Marshal.ReadIntPtr(activations,i*IntPtr.Size),result=0; Guid iid=TransformIid;
      if(((delegate* unmanaged[Stdcall]<nint,Guid*,nint*,int>)Fn(activate,33))(activate,&iid,&result)>=0) transform=result;
     }
     if(transform==0) throw new NotSupportedException("Не удалось открыть HEVC-декодировщик");
    } finally { for(int i=0;i<count;i++) { var activate=Marshal.ReadIntPtr(activations,i*IntPtr.Size); Release(ref activate); } Marshal.FreeCoTaskMem(activations); }
   }
   nint attrs=0;
   try {
    Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)Fn(transform,8))(transform,&attrs));
    Set32(attrs,LowLatency,1);
    if(!software && Get32(attrs,Aware)!=0) {
     try {
      Check(D3D11CreateDevice(0,1,0,0x820,0,0,7,out device,out _,out context));
      var multithread=Query(context,MultiThreadIid);
      if(multithread==0) throw new InvalidOperationException("D3D не поддерживает защиту потоков");
      try { ((delegate* unmanaged[Stdcall]<nint,int,int>)Fn(multithread,5))(multithread,1); } finally { Release(ref multithread); }
      Check(MFCreateDXGIDeviceManager(out var token,out manager));
      Check(((delegate* unmanaged[Stdcall]<nint,nint,uint,int>)Fn(manager,7))(manager,device,token));
      Message(2,manager); UsingDevice=true;
     } catch(Exception e) { Log.Write("DXVA недоступен, используем процессор",e); Release(ref manager); Release(ref context); Release(ref device); }
    }
   } finally { Release(ref attrs); }
   nint input=0;
   try {
    Check(MFCreateMediaType(out input)); SetGuid(input,Major,Video); SetGuid(input,Subtype,codec==1?H264:new Guid("43564548-0000-0010-8000-00aa00389b71"));
    Set64(input,FrameSize,((ulong)w<<32)|(uint)h); Set64(input,FrameRate,(30UL<<32)|1); Set32(input,Interlace,2);
    Check(((delegate* unmanaged[Stdcall]<nint,uint,nint,uint,int>)Fn(transform,15))(transform,0,input,0));
   } finally { Release(ref input); }
   SelectOutput(); Message(0x10000000); Message(0x10000003);
  } catch { Dispose(); throw; }
 }
 private void SelectOutput() {
  for(uint index=0;index<32;index++) {
   nint type=0;
   var hr=((delegate* unmanaged[Stdcall]<nint,uint,uint,nint*,int>)Fn(transform,14))(transform,0,index,&type);
   Check(hr);
   try {
    Guid key=Subtype,value=default;
    Check(((delegate* unmanaged[Stdcall]<nint,Guid*,Guid*,int>)Fn(type,10))(type,&key,&value));
    if(value!=Nv12) continue;
    Check(((delegate* unmanaged[Stdcall]<nint,uint,nint,uint,int>)Fn(transform,16))(transform,0,type,0));
    var size=Get64(type,FrameSize); width=(int)(size>>32); height=(int)(size&uint.MaxValue);
    stride=checked((int)Get32(type,Stride,(uint)width));
    if(width is < 16 or > 4096 || height is < 16 or > 4096 || (width&1)!=0 || (height&1)!=0 || stride<width || stride>8192) throw new InvalidDataException("Неподдерживаемые размеры кадра");
    return;
   } finally { Release(ref type); }
  }
  throw new InvalidOperationException("Декодировщик не поддерживает NV12");
 }
 internal void Feed(byte[] data,long phoneUs,Action<VideoFrame> frame) {
  nint sample=0,buffer=0;
  try {
   Check(MFCreateSample(out sample)); Check(MFCreateMemoryBuffer((uint)data.Length,out buffer));
   nint bytes=0; uint max=0,current=0;
   Check(((delegate* unmanaged[Stdcall]<nint,nint*,uint*,uint*,int>)Fn(buffer,3))(buffer,&bytes,&max,&current));
   try { Marshal.Copy(data,0,bytes,data.Length); } finally { Check(((delegate* unmanaged[Stdcall]<nint,int>)Fn(buffer,4))(buffer)); }
   Check(((delegate* unmanaged[Stdcall]<nint,uint,int>)Fn(buffer,6))(buffer,(uint)data.Length));
   Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)Fn(sample,42))(sample,buffer));
   Check(((delegate* unmanaged[Stdcall]<nint,long,int>)Fn(sample,36))(sample,checked(phoneUs*10)));
   Check(((delegate* unmanaged[Stdcall]<nint,long,int>)Fn(sample,38))(sample,333333));
   var hr=((delegate* unmanaged[Stdcall]<nint,uint,nint,uint,int>)Fn(transform,24))(transform,0,sample,0);
   if(hr==NotAccepting) { Drain(frame); hr=((delegate* unmanaged[Stdcall]<nint,uint,nint,uint,int>)Fn(transform,24))(transform,0,sample,0); }
   Check(hr); Drain(frame);
  } finally { Release(ref buffer); Release(ref sample); }
 }
 private void Drain(Action<VideoFrame> frame) {
  for(int count=0;count<32;count++) {
   var info=new OutputInfo(); Check(((delegate* unmanaged[Stdcall]<nint,uint,OutputInfo*,int>)Fn(transform,7))(transform,0,&info));
   nint sample=0,buffer=0;
   var output=new Output();
   try {
    if((info.Flags&0x100)==0) {
     Check(MFCreateSample(out sample));
     uint size=Math.Max(info.Size,(uint)(stride*height*3/2));
     Check(info.Alignment>1?MFCreateAlignedMemoryBuffer(size,info.Alignment-1,out buffer):MFCreateMemoryBuffer(size,out buffer));
     Check(((delegate* unmanaged[Stdcall]<nint,nint,int>)Fn(sample,42))(sample,buffer)); output.Sample=sample;
    }
    uint status=0;
    int hr=((delegate* unmanaged[Stdcall]<nint,uint,uint,Output*,uint*,int>)Fn(transform,25))(transform,0,1,&output,&status);
    if(hr==NeedMore) return;
    if(hr==StreamChange) { SelectOutput(); continue; }
    Check(hr);
    if(output.Sample==0) continue;
    frame(ReadFrame(output.Sample));
   } finally {
    Release(ref output.Events);
    if(output.Sample!=sample) Release(ref output.Sample);
    Release(ref buffer); Release(ref sample);
   }
  }
 }
 private VideoFrame ReadFrame(nint sample) {
  nint buffer=0; byte[]? nv=null;
  try {
   Check(((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)Fn(sample,40))(sample,0,&buffer));
   var two=Query(buffer,Buffer2DIid); int pitch=stride;
   try {
    if(two!=0) {
     uint length=0; Check(((delegate* unmanaged[Stdcall]<nint,uint*,int>)Fn(two,7))(two,&length));
     if(length<width*height*3/2 || length>64*1024*1024) throw new InvalidDataException("Некорректный буфер NV12");
     nv=ArrayPool<byte>.Shared.Rent((int)length);
     fixed(byte* target=nv) Check(((delegate* unmanaged[Stdcall]<nint,byte*,uint,int>)Fn(two,8))(two,target,length));
     pitch=width;
    } else {
     nint source=0; uint max=0,length=0;
     Check(((delegate* unmanaged[Stdcall]<nint,nint*,uint*,uint*,int>)Fn(buffer,3))(buffer,&source,&max,&length));
     try {
      if(length<pitch*height*3/2 || length>64*1024*1024) throw new InvalidDataException("Некорректный буфер NV12");
      nv=ArrayPool<byte>.Shared.Rent((int)length); Marshal.Copy(source,nv,0,(int)length);
     } finally { Check(((delegate* unmanaged[Stdcall]<nint,int>)Fn(buffer,4))(buffer)); }
    }
   } finally { Release(ref two); }
   long time=0; Check(((delegate* unmanaged[Stdcall]<nint,long*,int>)Fn(sample,35))(sample,&time));
   var pixels=ArrayPool<byte>.Shared.Rent(width*height*4);
   try { ConvertNv12(nv!,pitch,width,height,pixels); return new(width,height,pixels,time/10,UsingDevice?"DXVA/D3D11":"Процессор"); }
   catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
  } finally { if(nv!=null) ArrayPool<byte>.Shared.Return(nv); Release(ref buffer); }
 }
 internal static void ConvertNv12(byte[] input,int pitch,int width,int height,byte[] output) {
  Parallel.For(0,height/2,row=> {
   for(int dy=0;dy<2;dy++) {
    int y=row*2+dy;
    for(int x=0;x<width;x++) {
     int c=Math.Max(0,input[y*pitch+x]-16),uv=pitch*height+row*pitch+(x&~1),u=input[uv]-128,v=input[uv+1]-128,o=(y*width+x)*4;
     output[o]=(byte)Math.Clamp((298*c+541*u+128)>>8,0,255);
     output[o+1]=(byte)Math.Clamp((298*c-55*u-136*v+128)>>8,0,255);
     output[o+2]=(byte)Math.Clamp((298*c+459*v+128)>>8,0,255); output[o+3]=255;
    }
   }
  });
 }
 public void Dispose() {
  Release(ref transform); Release(ref manager); Release(ref context); Release(ref device);
  if(mf) { MFShutdown(); mf=false; } if(com) { CoUninitialize(); com=false; }
 }
}
