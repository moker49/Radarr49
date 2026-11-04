import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import { kinds } from 'Helpers/Props';
import { clearPendingChanges, set } from 'Store/Actions/baseActions';
import { saveQualityProfile, setQualityProfileValue } from 'Store/Actions/settingsActions';
import createProviderSettingsSelector from 'Store/Selectors/createProviderSettingsSelector';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import QualityProfileSizeDefinitions from './QualityProfileSizeDefinitions';

class QualityProfileSizeDefinitionsConnector extends Component {

  componentDidMount() {
    this.props.onChildMounted(this.onSavePress);
    this.notifyParent();
  }

  componentDidUpdate(prevProps) {
    if (prevProps.isSaving !== this.props.isSaving || prevProps.hasPendingChanges !== this.props.hasPendingChanges) {
      this.notifyParent();
    }
  }

  componentWillUnmount() {
    this.props.clearPendingChanges({ section: 'settings.qualityProfiles' });
    this.props.set({ section: 'settings.qualityProfiles', saveError: null });
  }

  notifyParent = () => {
    this.props.onChildStateChange({ isSaving: this.props.isSaving, hasPendingChanges: this.props.hasPendingChanges });
  };

  onSavePress = () => {
    if (!this.props.isSaving) {
      this.props.saveQualityProfile({ id: this.props.id });
    }
  };

  onInputChange = (change) => {
    this.props.setQualityProfileValue(change);
  };

  render() {
    const { item, saveError } = this.props;

    return (
      <FieldSet legend={translate('ProfileSizeSettings')}>
        {saveError ? <Alert kind={kinds.DANGER}>{getErrorMessage(saveError, translate('QualitySizeSettingsSaveError'))}</Alert> : null}
        <QualityProfileSizeDefinitions
          qualityProfileItems={item.items.value}
          definitions={item.sizeDefinitions}
          onChange={this.onInputChange}
        />
      </FieldSet>
    );
  }
}

QualityProfileSizeDefinitionsConnector.propTypes = {
  id: PropTypes.number.isRequired,
  item: PropTypes.object.isRequired,
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
  createProviderSettingsSelector('qualityProfiles'),
  { saveQualityProfile, setQualityProfileValue, clearPendingChanges, set }
)(QualityProfileSizeDefinitionsConnector);
