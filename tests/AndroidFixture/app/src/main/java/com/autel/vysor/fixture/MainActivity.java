package com.autel.vysor.fixture;

import android.app.Activity;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;

/**
 * Intentionally disposable, debuggable-by-build-type fixture for REAL USB devices.
 * Records received text in app-private input.txt (readable via adb shell run-as).
 * Does not collect or upload personal data or modify other application packages.
 */
public final class MainActivity extends Activity {
    private void persist(String value) {
        try (FileOutputStream out = openFileOutput("input.txt", MODE_PRIVATE)) {
            out.write(value.getBytes(StandardCharsets.UTF_8));
        } catch (Exception error) {
            android.util.Log.e("AutelE2E", "Unable to persist test text", error);
        }
    }

    @Override
    public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        layout.setPadding(25, 36, 25, 25);
        TextView heading = new TextView(this);
        heading.setText("AUTEL PHYSICAL E2E TEST APP\nType with the Windows keyboard.");
        heading.setTextSize(18);
        layout.addView(heading, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        EditText text = new EditText(this);
        text.setId(R.id.test_input);
        text.setSingleLine(false);
        text.setMinLines(3);
        text.setHint("AUTEL_INPUT_TARGET");
        layout.addView(text, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        setContentView(layout);
        persist("");
        text.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                persist(s.toString());
            }
            @Override public void afterTextChanged(Editable value) {}
        });
        text.requestFocus();
    }
}
