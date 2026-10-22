                    case AcquisitionConnectionType.Narou:
                        var ncode = download.ExternalId?.Split(':') is ["narou", var sourceKey, ..] && NarouClient.IsNcode(sourceKey)
                            ? sourceKey : throw new DirectSourceException("acquisition_grab_failed");
                        if (!address.Host.Equals("ncode.syosetu.com", StringComparison.OrdinalIgnoreCase)
                            || !address.AbsolutePath.Equals("/" + ncode + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new DirectSourceException("acquisition_grab_failed");
                        }

                        await _narou.WriteEpubAsync(ncode, destination, paths.Settings.MaxJobBytes, Reserve, ct);
                        break;                    case AcquisitionConnectionType.Narou:
                        var ncode = download.ExternalId?.Split(':') is ["narou", var sourceKey, ..] && NarouClient.IsNcode(sourceKey)
                            ? sourceKey : throw new DirectSourceException("acquisition_grab_failed");
                        if (!address.Host.Equals("ncode.syosetu.com", StringComparison.OrdinalIgnoreCase)
                            || !address.AbsolutePath.Equals("/" + ncode + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new DirectSourceException("acquisition_grab_failed");
                        }

                        await _narou.WriteEpubAsync(ncode, destination, paths.Settings.MaxJobBytes, Reserve, ct);
                        break;

