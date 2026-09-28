package app.relaxkonos.mobile.core.net

/**
 * The operating system class of the host a saved connection points at.
 *
 * A product mark rather than a version list: it exists so a saved connection can carry the same
 * operating-system mark the connection list legends, and it decides nothing about what the server can
 * do — capabilities do that.
 *
 * [Unknown] deliberately covers two cases: "the server answered that it is not one of these" and "this
 * build does not know the name the server gave". Both mean the same thing on screen — show the generic
 * mark and claim nothing — and keeping them apart would only invite a client to guess.
 */
enum class HostOperatingSystemKind {
    Unknown,
    Ubuntu,
    Windows10,
    Windows11,
    WindowsServer,
    ;

    companion object {
        /**
         * Reads the wire name (`RelaxKonOS.Protocol.HostOperatingSystemKind`).
         *
         * Case-insensitive because the server serialises an enum as its camelCase name and the members
         * here are the same words. A name this build does not know is [Unknown], never an error: a
         * newer server naming a system this version has no mark for must still answer the list rather
         * than fail it.
         */
        fun fromWire(value: String?): HostOperatingSystemKind =
            entries.firstOrNull { it.name.equals(value, ignoreCase = true) } ?: Unknown
    }
}
