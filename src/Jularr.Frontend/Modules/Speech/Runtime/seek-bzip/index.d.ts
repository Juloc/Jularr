declare const Bunzip: { decode(input: { readByte(): number; read(buffer: Uint8Array, offset: number, count: number): number }, output: { writeByte(value: number): void }, multistream: boolean): void };
export default Bunzip;
