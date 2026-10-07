package com.jcraft.jsch;

/** Uses the library's package-private constructor without production reflection. */
public final class ReviewDisconnectException extends JSchSessionDisconnectException {
    public ReviewDisconnectException(int reason, String description) {
        super("disconnect", reason, description, "en");
    }
}
