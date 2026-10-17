package ru.lenscast

import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.SocketTimeoutException

/** An idle timeout is allowed ONLY before the first header byte. */
object Framing {
 fun read(input: DataInputStream, bodyTimeout: () -> Unit = {}): ByteArray? {
  val first = try { input.readUnsignedByte() } catch (_: SocketTimeoutException) { return null }
  bodyTimeout()
  val size = (first shl 24) or (input.readUnsignedByte() shl 16) or
   (input.readUnsignedByte() shl 8) or input.readUnsignedByte()
  require(size in 2..65536) { "Недопустимый размер сообщения" }
  return ByteArray(size).also { input.readFully(it) }
 }
 fun write(output: DataOutputStream, bytes: ByteArray) {
  require(bytes.size in 2..65536) { "Недопустимый размер сообщения" }
  output.writeInt(bytes.size); output.write(bytes); output.flush()
 }
}
