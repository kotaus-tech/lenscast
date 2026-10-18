package ru.lenscast.gesturefixture;

import android.app.Activity;
import android.os.Bundle;
import android.view.MotionEvent;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

/** CI-only target in a separate APK. Never included in Lenscast or its release. */
public final class TargetActivity extends Activity {
 private int clicks, holds, ups, moves;
 private long duration;
 private boolean pressed;
 private TextView stats;
 @Override public void onCreate(Bundle saved) {
  super.onCreate(saved);
  LinearLayout root=new LinearLayout(this); root.setOrientation(LinearLayout.VERTICAL);
  stats=new TextView(this); stats.setTextSize(20); root.addView(stats);
  Button target=new Button(this); target.setId(R.id.gesture_target); target.setText("Область жеста");
  root.addView(target,new LinearLayout.LayoutParams(-1,0,1));
  target.setOnClickListener(v->{ clicks++; update(); });
  target.setOnLongClickListener(v->{ holds++; update(); return true; });
  target.setOnTouchListener((v,event)->{
   switch(event.getActionMasked()) {
    case MotionEvent.ACTION_DOWN: pressed=true; break;
    case MotionEvent.ACTION_MOVE: moves++; break;
    case MotionEvent.ACTION_UP: pressed=false; ups++; duration=event.getEventTime()-event.getDownTime(); break;
    case MotionEvent.ACTION_CANCEL: pressed=false; break;
   }
   update(); return false;
  });
  setContentView(root); update();
 }
 private void update() { stats.setText("Нажатий: "+clicks+"; удержаний: "+holds+"; отпусканий: "+ups+"; движений: "+moves+"; длительность: "+duration+"; касание: "+(pressed?1:0)); }
}
