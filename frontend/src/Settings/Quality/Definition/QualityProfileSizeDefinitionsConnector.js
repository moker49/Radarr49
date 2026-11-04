import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import Alert from 'Components/Alert';
import { kinds } from 'Helpers/Props';
import { clearPendingChanges, set } from 'Store/Actions/baseActions';
import { saveQualityProfile, setQualityProfileValue } from 'Store/Actions/settingsActions';
import createProviderSettingsSelector from 'Store/Selectors/createProviderSettingsSelector';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import QualityDefinition from './QualityDefinition';
import QualityDefinitions from './QualityDefinitions';

function createMapStateToProps() {
  return createSelector(
    createProviderSettingsSelector('qualityProfiles'),
    (state) => state.settings.qualityDefinitions,
    (state) => state.settings.advancedSettings,
    (profile, definitions, advancedSettings) => ({ ...profile, globalDefinitions: definitions.items, advancedSettings })
  );
}

class QualityProfileSizeDefinitionsConnector extends Component {

  componentDidMount() {
    this.props.onChildMounted(this.onSavePress, { onUseGlobalChange: this.onUseGlobalChange, onCopyGlobal: this.onCopyGlobal });
    this.notifyParent();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.isSaving !== this.props.isSaving || prevProps.hasPendingChanges !== this.props.hasPendingChanges || prevProps.item.useGlobalSizeDefinitions?.value !== this.props.item.useGlobalSizeDefinitions?.value) {
      this.notifyParent();
    }
  }

  componentWillUnmount() {
    this.props.clearPendingChanges({ section: 'settings.qualityProfiles' });
    this.props.set({ section: 'settings.qualityProfiles', saveError: null });
  }

  notifyParent = () => {
    this.props.onChildStateChange({
      isSaving: this.props.isSaving,
      hasPendingChanges: this.props.hasPendingChanges,
      useGlobalValues: !!this.props.item.useGlobalSizeDefinitions?.value
    });
  };

  onSavePress = () => {
    if (!this.props.isSaving) {
      this.props.saveQualityProfile({ id: this.props.id });
    }
  };

  onUseGlobalChange = ({ value }) => {
    this.props.setQualityProfileValue({ name: 'useGlobalSizeDefinitions', value });
  };

  onCopyGlobal = () => {
    const value = this.props.globalDefinitions.map(({ quality, minSize, preferredSize, maxSize }) => ({ qualityId: quality.id, minSize, preferredSize, maxSize }));
    this.props.setQualityProfileValue({ name: 'sizeDefinitions', value });
  };

  onSizeChange = (qualityId, sizes) => {
    if (this.props.isSaving || this.props.item.useGlobalSizeDefinitions?.value) {
      return;
    }

    const values = this.props.item.sizeDefinitions?.value || [];
    this.props.setQualityProfileValue({ name: 'sizeDefinitions', value: [
      ...values.filter((definition) => definition.qualityId !== qualityId),
      { qualityId, ...sizes }
    ] });
  };

  render() {
    const { item, saveError, globalDefinitions, advancedSettings, isSaving } = this.props;
    const useGlobalValues = !!item.useGlobalSizeDefinitions?.value;
    const custom = item.sizeDefinitions?.value || [];
    const items = globalDefinitions.map((definition) => {
      const override = useGlobalValues ? null : custom.find((entry) => entry.qualityId === definition.quality.id);
      return {
        ...definition,
        ...(override || {}),
        isDisabled: useGlobalValues || isSaving,
        onSizeChange: (sizes) => this.onSizeChange(definition.quality.id, sizes)
      };
    });

    return (
      <div>
        {saveError ? <Alert kind={kinds.DANGER}>{getErrorMessage(saveError, translate('QualitySizeSettingsSaveError'))}</Alert> : null}
        <QualityDefinitions
          items={items}
          isFetching={false}
          isPopulated={true}
          advancedSettings={advancedSettings}
          showTitles={false}
          definitionComponent={QualityDefinition}
        />
        {item.sizeDefinitions?.errors?.map((error) => <Alert key={error.message} kind={kinds.DANGER}>{error.message}</Alert>)}
      </div>
    );
  }
}

QualityProfileSizeDefinitionsConnector.propTypes = {
  id: PropTypes.number.isRequired,
  item: PropTypes.object.isRequired,
  globalDefinitions: PropTypes.arrayOf(PropTypes.object).isRequired,
  advancedSettings: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  hasPendingChanges: PropTypes.bool.isRequired,
  saveError: PropTypes.object,
  saveQualityProfile: PropTypes.func.isRequired,
  setQualityProfileValue: PropTypes.func.isRequired,
  clearPendingChanges: PropTypes.func.isRequired,
  set: PropTypes.func.isRequired,
  onChildMounted: PropTypes.func.isRequired,
  onChildStateChange: PropTypes.func.isRequired
};

export default connect(
  createMapStateToProps,
  { saveQualityProfile, setQualityProfileValue, clearPendingChanges, set }
)(QualityProfileSizeDefinitionsConnector);
