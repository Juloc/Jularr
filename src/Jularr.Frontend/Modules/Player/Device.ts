// What kind of device the player runs on, for its arrangement: a phone has a touch screen whose short side is under 600 px, whatever the width of the picture
// (a phone in fullscreen is wide); a tablet is a touch device that is larger; anything else has a pointer.
export type DeviceKind = "phone" | "tablet" | "pointer";

export const PhoneShortSide = 600;

export function deviceKind(touch: boolean, screenWidth: number, screenHeight: number): DeviceKind {
    if (!touch) return "pointer";
    return Math.min(screenWidth, screenHeight) < PhoneShortSide ? "phone" : "tablet";
}
