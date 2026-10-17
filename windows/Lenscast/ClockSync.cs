namespace Lenscast;
/// <summary>Clock offset from the minimum-RTT midpoint estimate; not video latency.</summary>
internal sealed class ClockSync {
 internal double BestRttUs { get; private set; } = double.PositiveInfinity;
 internal double PhoneMinusPcUs { get; private set; }
 internal void Observe(long sentPcUs, long receivedPcUs, long phoneUs) {
  if (sentPcUs < 0 || receivedPcUs < sentPcUs || phoneUs < 0) return;
  double rtt = receivedPcUs - sentPcUs;
  if (rtt >= BestRttUs) return;
  BestRttUs = rtt;
  PhoneMinusPcUs = phoneUs - (sentPcUs + rtt / 2);
 }
}
