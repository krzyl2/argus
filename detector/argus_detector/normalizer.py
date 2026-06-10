"""
Per-entity online min-max normalization.

Thin wrapper around river.preprocessing.MinMaxScaler.
EntityDetector uses this internally; exposed as a module so Plan 2+ can swap.
"""

from river import preprocessing


class OnlineMinMaxScaler:
    """Online min-max scaler using River's MinMaxScaler.

    Learns bounds from the stream; clips values to [0, 1] once range stabilizes.
    During warm-up, values outside the observed range are clipped by River itself.
    """

    def __init__(self) -> None:
        self._scaler = preprocessing.MinMaxScaler()

    def learn_transform(self, value: float) -> dict[str, float]:
        """Update bounds and return the normalized value as a feature dict.

        Args:
            value: raw sensor reading

        Returns:
            dict {"value": normalized_value_in_[0,1]}
        """
        x = {"value": value}
        self._scaler.learn_one(x)
        x_norm = self._scaler.transform_one(x)
        return x_norm
