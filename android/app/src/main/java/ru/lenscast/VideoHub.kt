package ru.lenscast

import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.security.MessageDigest
import java.util.concurrent.ArrayBlockingQueue

/** One authenticated, bounded video consumer; encoder never waits for network I/O. */
object VideoHub {
 data class Packet(val type: Int, val time: Long, val data: ByteArray, val codec: Int = 1)
 private class Viewer(val socket: Socket) {
  val packets = ArrayBlockingQueue<Packet>(3)
  var waitingKey = true
  @Volatile var writingAt=0L
 }
 private val pending=java.util.concurrent.atomic.AtomicInteger()
 private var ticket = ""
 private var usb = false
 private var viewer: Viewer? = null
 private var config: Packet? = null
 @Volatile var requestKey: (() -> Unit)? = null
 @Volatile var frames = 0L
 @Volatile var bytes = 0L
 @Volatile var dropped = 0L
 @Synchronized fun session(value: String, local: Boolean) {
  viewer?.socket?.runCatching { close() }; viewer = null
  ticket=value; usb=local
 }
 @Synchronized fun checkHealth() { viewer?.let { if(it.writingAt!=0L && System.nanoTime()/1_000_000-it.writingAt>2000) it.socket.runCatching { close() } } }
 @Synchronized fun end(value: String) { if(ticket==value) session("", false) }
 @Synchronized fun stopCapture() { clearFormat(); viewer?.socket?.runCatching { close() }; viewer=null }
 @Synchronized fun clearFormat() { config=null; viewer?.let { it.packets.clear(); it.waitingKey=true } }
 @Synchronized fun offer(packet: Packet) {
  if(packet.type==0) {
   config=packet
   viewer?.let { it.packets.clear(); it.waitingKey=true; it.packets.offer(packet) }
   return
  }
  frames++; bytes+=packet.data.size
  val v=viewer ?: return
  if(v.waitingKey && packet.type!=2) { dropped++; return }
  if(!v.packets.offer(packet)) {
   dropped++; v.packets.clear(); v.waitingKey=true
   config?.let { v.packets.offer(it) }
   if(packet.type==2) { v.packets.offer(packet); v.waitingKey=false } else requestKey?.invoke()
  } else if(packet.type==2) v.waitingKey=false
 }
 fun serve(server: ServerSocket, local: Boolean, execute: (Runnable)->Unit, log: (Throwable)->Unit) {
  execute(Runnable {
   while(!server.isClosed) try {
    val socket=server.accept().apply { tcpNoDelay=true; soTimeout=5000; sendBufferSize=64*1024 }
    if(pending.incrementAndGet()>8) { pending.decrementAndGet(); socket.close(); continue }
    execute(Runnable {
     var own: Viewer?=null
     try {
      val input=DataInputStream(socket.getInputStream())
      val raw=Framing.read(input) {} ?: return@Runnable
      val hello=JSONObject(String(raw,Charsets.UTF_8))
      synchronized(this) {
       val supplied=hello.optString("ticket")
       if(hello.optInt("version")!=1 || ticket.isEmpty() || usb!=local ||
        !MessageDigest.isEqual(ticket.toByteArray(),supplied.toByteArray()) || viewer!=null) return@Runnable
       own=Viewer(socket); viewer=own
       config?.let { own!!.packets.offer(it) }
      }
      requestKey?.invoke()
      val output=DataOutputStream(socket.getOutputStream())
      var sequence=0
      while(!socket.isClosed) {
       val packet=own!!.packets.poll(1,java.util.concurrent.TimeUnit.SECONDS)
       if(packet==null) {
        socket.soTimeout=10
        try { input.read(); break } catch(_: java.net.SocketTimeoutException) { continue }
       }
       own!!.writingAt=System.nanoTime()/1_000_000
       output.writeInt(0x4C435631); output.writeByte(1); output.writeByte(packet.type)
       output.writeByte(packet.codec); output.writeByte(0); output.writeLong(packet.time)
       output.writeInt(packet.data.size); output.writeInt(sequence++); output.write(packet.data)
       own!!.writingAt=0
      }
     } catch(e: Exception) { if(!socket.isClosed) log(if(e is org.json.JSONException) IllegalArgumentException("Недопустимый заголовок видеоканала") else e) }
     finally {
      pending.decrementAndGet()
      synchronized(this) { if(own!=null && viewer===own) viewer=null }
      socket.runCatching { close() }
     }
    })
   } catch(e: Exception) { if(!server.isClosed) log(e) }
  })
 }
}
