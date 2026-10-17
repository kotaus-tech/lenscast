package ru.lenscast

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import java.net.Inet4Address

object NetworkAddresses {
 @Suppress("DEPRECATION")
 fun wifi(context: Context): String {
  val manager=context.getSystemService(ConnectivityManager::class.java)
  return manager.allNetworks.filter { network ->
   manager.getNetworkCapabilities(network)?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
  }.flatMap { manager.getLinkProperties(it)?.linkAddresses.orEmpty() }
   .map { it.address }.filterIsInstance<Inet4Address>().filterNot { it.isLoopbackAddress }
   .mapNotNull { it.hostAddress }.distinct().joinToString(" · ")
 }
}
